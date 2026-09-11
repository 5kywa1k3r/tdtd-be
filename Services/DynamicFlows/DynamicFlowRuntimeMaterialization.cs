using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.WorkAssignments.Queue;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowRuntimeActivationPolicy
{
    bool CandidateExecutionEnabled { get; }
    bool P6CandidateExecutionEnabled { get; }
    bool P7MappingCandidateExecutionEnabled { get; }
    bool P7MappingLifecycleExecutionEnabled { get; }
    bool IsP6CandidateArchetypeEnabled(string? archetypeId);
    bool CanExecuteP6CandidatePin(
        string? catalogVersion,
        string? catalogSemanticHash,
        string? archetypeId);
    bool CanExecuteP7MappingPin(
        string? catalogVersion,
        string? catalogSemanticHash);
}

public sealed class DynamicFlowRuntimeActivationPolicy : IDynamicFlowRuntimeActivationPolicy
{
    public const string TestingActivationKey =
        "DynamicFlowRuntime:TestingCandidateActivationEnabled";
    public const string TestingActivationThroughKey =
        "DynamicFlowRuntime:TestingCandidateActivationThrough";
    public const string TestingP7MappingActivationKey =
        "DynamicFlowRuntime:TestingP7MappingCandidateActivationEnabled";
    public const string TestingP7MappingActivationThroughKey =
        "DynamicFlowMapping:TestingCandidateActivationThrough";

    private readonly bool _sealedP6CatalogActivationEnabled;
    private readonly bool _testingP6CandidateActivationEnabled;
    private readonly bool _testingP7MappingCandidateActivationEnabled;
    private readonly int? _p6CandidateActivationThrough;

    public DynamicFlowRuntimeActivationPolicy(
        IHostEnvironment environment,
        IConfiguration configuration)
        : this(
            environment,
            configuration,
            DynamicFlowP6CatalogCandidate.ActivationEnabled)
    {
    }

    internal DynamicFlowRuntimeActivationPolicy(
        IHostEnvironment environment,
        IConfiguration configuration,
        bool sealedP6CatalogActivationEnabled)
    {
        CandidateExecutionEnabled =
            DynamicFlowRuntimeCatalogCandidate.ActivationEnabled;
        _sealedP6CatalogActivationEnabled =
            sealedP6CatalogActivationEnabled;
        var configuredThrough =
            configuration.GetValue<int?>(TestingActivationThroughKey);
        _p6CandidateActivationThrough =
            configuredThrough is >= 3 and <= 12
                ? configuredThrough
                : null;
        _testingP6CandidateActivationEnabled =
            environment.IsEnvironment("Testing") &&
            configuration.GetValue<bool>(TestingActivationKey) &&
            _p6CandidateActivationThrough is not null;
        _testingP7MappingCandidateActivationEnabled =
            environment.IsEnvironment("Testing") &&
            configuration.GetValue<bool>(TestingP7MappingActivationKey);
        P6CandidateExecutionEnabled =
            _sealedP6CatalogActivationEnabled ||
            _testingP6CandidateActivationEnabled;
        P7MappingCandidateExecutionEnabled =
            DynamicFlowP7CatalogCandidate.ActivationEnabled ||
            _testingP7MappingCandidateActivationEnabled;
        var p7MappingActivationThrough = configuration.GetValue<int?>(
            TestingP7MappingActivationThroughKey);
        P7MappingLifecycleExecutionEnabled =
            P7MappingCandidateExecutionEnabled &&
            (p7MappingActivationThrough ?? 9) >= 9;
    }

    public bool CandidateExecutionEnabled { get; }
    public bool P6CandidateExecutionEnabled { get; }
    public bool P7MappingCandidateExecutionEnabled { get; }
    public bool P7MappingLifecycleExecutionEnabled { get; }

    public bool IsP6CandidateArchetypeEnabled(string? archetypeId)
    {
        var archetypeNumber = archetypeId switch
        {
            "FLOW-T01" => 1,
            "FLOW-T02" => 2,
            "FLOW-T03" => 3,
            "FLOW-T04" => 4,
            "FLOW-T05" => 5,
            "FLOW-T06" => 6,
            "FLOW-T07" => 7,
            "FLOW-T08" => 8,
            "FLOW-T09" => 9,
            "FLOW-T10" => 10,
            "FLOW-T11" => 11,
            "FLOW-T12" => 12,
            _ => 0
        };

        if (_sealedP6CatalogActivationEnabled ||
            P7MappingCandidateExecutionEnabled)
            return archetypeNumber is >= 1 and <= 12;

        if (!_testingP6CandidateActivationEnabled ||
            _p6CandidateActivationThrough is not int activationThrough)
        {
            return false;
        }

        return archetypeNumber is >= 3 and <= 12 &&
               archetypeNumber <= activationThrough;
    }

    public bool CanExecuteP6CandidatePin(
        string? catalogVersion,
        string? catalogSemanticHash,
        string? archetypeId)
        => ((P6CandidateExecutionEnabled &&
             DynamicFlowRuntimeEligibilityPolicy.IsExactP6Catalog(
                 catalogVersion,
                 catalogSemanticHash)) ||
            CanExecuteP7MappingPin(catalogVersion, catalogSemanticHash)) &&
           IsP6CandidateArchetypeEnabled(archetypeId);

    public bool CanExecuteP7MappingPin(
        string? catalogVersion,
        string? catalogSemanticHash)
        => P7MappingCandidateExecutionEnabled &&
           DynamicFlowRuntimeEligibilityPolicy.IsP7MappingCatalog(
               catalogVersion,
               catalogSemanticHash);
}

public static class DynamicFlowRuntimeMaterializationOperations
{
    public const string MaterializeEntryAssignment = "MATERIALIZE_ENTRY_ASSIGNMENT";
    public const string MaterializeSequentialAssignment = "MATERIALIZE_SEQUENTIAL_ASSIGNMENT";
    public const string MaterializeForkBranchAssignment = "MATERIALIZE_FORK_BRANCH_ASSIGNMENT";
    public const string MaterializeReviewAttemptAssignment =
        "MATERIALIZE_REVIEW_ATTEMPT_ASSIGNMENT";
    public const string MaterializeSupplementalAssignment =
        "MATERIALIZE_SUPPLEMENTAL_ASSIGNMENT";
}

public static class DynamicFlowRuntimeEventTypes
{
    public const string LaunchIntentCommitted = "LAUNCH_INTENT_COMMITTED";
    public const string EntryAssignmentMaterialized = "ENTRY_ASSIGNMENT_MATERIALIZED";
    public const string SequentialForwardAccepted = "SEQUENTIAL_FORWARD_ACCEPTED";
    public const string SequentialAssignmentMaterialized = "SEQUENTIAL_ASSIGNMENT_MATERIALIZED";
    public const string ParallelForkAccepted = "PARALLEL_FORK_ACCEPTED";
    public const string ForkBranchAssignmentMaterialized = "FORK_BRANCH_ASSIGNMENT_MATERIALIZED";
    public const string ReviewAttemptAssignmentMaterialized =
        "REVIEW_ATTEMPT_ASSIGNMENT_MATERIALIZED";
    public const string ReviewReturnAttemptCreated =
        DynamicFlowReviewLoopTopologyContract.ReviewAttemptCreatedEvent;
    public const string ReviewCycleExhausted =
        DynamicFlowReviewLoopTopologyContract.ReviewCycleExhaustedEvent;
    public const string RecoveryStarted = "RECOVERY_STARTED";
    public const string RecoveryReconciled = "RECOVERY_RECONCILED";
    public const string RecoveryCompleted = "RECOVERY_COMPLETED";
    public const string CompensationApplied = "COMPENSATION_APPLIED";
    public const string SupplementalAssignmentMaterialized =
        DynamicFlowSupplementalTopologyContract.MaterializedEvent;
}

public static class DynamicFlowRuntimeFaultPoints
{
    public const string BeforeIntentTransaction = "BEFORE_INTENT_TRANSACTION";
    public const string BeforeReceiptWrite = "BEFORE_RECEIPT_WRITE";
    public const string AfterReceiptWrite = "AFTER_RECEIPT_WRITE";
    public const string BeforeInstanceWrite = "BEFORE_INSTANCE_WRITE";
    public const string AfterInstanceWrite = "AFTER_INSTANCE_WRITE";
    public const string BeforeSnapshotWrite = "BEFORE_SNAPSHOT_WRITE";
    public const string AfterSnapshotWrite = "AFTER_SNAPSHOT_WRITE";
    public const string BeforeStepWrite = "BEFORE_STEP_WRITE";
    public const string AfterStepWrite = "AFTER_STEP_WRITE";
    public const string BeforeEventWrite = "BEFORE_EVENT_WRITE";
    public const string AfterEventWrite = "AFTER_EVENT_WRITE";
    public const string BeforeOutboxWrite = "BEFORE_OUTBOX_WRITE";
    public const string AfterOutboxWrite = "AFTER_OUTBOX_WRITE";
    public const string BeforeBindingClaimWrite = "BEFORE_BINDING_CLAIM_WRITE";
    public const string AfterBindingClaimWrite = "AFTER_BINDING_CLAIM_WRITE";
    public const string AfterIntentCommit = "AFTER_INTENT_COMMIT";
    public const string AfterOutboxClaim = "AFTER_OUTBOX_CLAIM";
    public const string BeforeAssignmentWrite = "BEFORE_ASSIGNMENT_WRITE";
    public const string AfterAssignmentWrite = "AFTER_ASSIGNMENT_WRITE";
    public const string BeforeBindingWrite = "BEFORE_BINDING_WRITE";
    public const string AfterBindingWrite = "AFTER_BINDING_WRITE";
    public const string BeforeDocRoleProjection = "BEFORE_DOCROLE_PROJECTION";
    public const string AfterDocRoleProjection = "AFTER_DOCROLE_PROJECTION";
    public const string BeforePeriodWrite = "BEFORE_PERIOD_WRITE";
    public const string AfterPeriodWrite = "AFTER_PERIOD_WRITE";
    public const string BeforeQueueWrite = "BEFORE_QUEUE_WRITE";
    public const string AfterQueueWrite = "AFTER_QUEUE_WRITE";
    public const string BeforeOutboxComplete = "BEFORE_OUTBOX_COMPLETE";
    public const string AfterOutboxComplete = "AFTER_OUTBOX_COMPLETE";
    public const string BeforeStepCompleteWrite = "BEFORE_STEP_COMPLETE_WRITE";
    public const string AfterStepCompleteWrite = "AFTER_STEP_COMPLETE_WRITE";
    public const string BeforeBranchEventWrite = "BEFORE_BRANCH_EVENT_WRITE";
    public const string AfterBranchEventWrite = "AFTER_BRANCH_EVENT_WRITE";
    public const string BeforeInstanceFinalizeWrite = "BEFORE_INSTANCE_FINALIZE_WRITE";
    public const string AfterInstanceFinalizeWrite = "AFTER_INSTANCE_FINALIZE_WRITE";
    public const string BeforeReceiptFinalizeWrite = "BEFORE_RECEIPT_FINALIZE_WRITE";
    public const string AfterReceiptFinalizeWrite = "AFTER_RECEIPT_FINALIZE_WRITE";
    public const string BeforeRetryStateWrite = "BEFORE_RETRY_STATE_WRITE";
    public const string AfterRetryStateWrite = "AFTER_RETRY_STATE_WRITE";
    public const string BeforeRecoveryEventWrite = "BEFORE_RECOVERY_EVENT_WRITE";
    public const string AfterRecoveryEventWrite = "AFTER_RECOVERY_EVENT_WRITE";
    public const string BeforeLedgerRepairWrite = "BEFORE_LEDGER_REPAIR_WRITE";
    public const string AfterLedgerRepairWrite = "AFTER_LEDGER_REPAIR_WRITE";
    public const string BeforeCompensationWrite = "BEFORE_COMPENSATION_WRITE";
    public const string AfterCompensationWrite = "AFTER_COMPENSATION_WRITE";
    public const string BeforeCompensationProjection = "BEFORE_COMPENSATION_PROJECTION";
    public const string AfterCompensationProjection = "AFTER_COMPENSATION_PROJECTION";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[]
        {
            BeforeIntentTransaction,
            BeforeReceiptWrite,
            AfterReceiptWrite,
            BeforeInstanceWrite,
            AfterInstanceWrite,
            BeforeSnapshotWrite,
            AfterSnapshotWrite,
            BeforeStepWrite,
            AfterStepWrite,
            BeforeEventWrite,
            AfterEventWrite,
            BeforeOutboxWrite,
            AfterOutboxWrite,
            BeforeBindingClaimWrite,
            AfterBindingClaimWrite,
            AfterIntentCommit,
            BeforeAssignmentWrite,
            AfterAssignmentWrite,
            BeforeBindingWrite,
            AfterBindingWrite,
            BeforeDocRoleProjection,
            AfterDocRoleProjection,
            BeforePeriodWrite,
            AfterPeriodWrite,
            BeforeQueueWrite,
            AfterQueueWrite,
            BeforeOutboxComplete,
            AfterOutboxComplete,
            BeforeStepCompleteWrite,
            AfterStepCompleteWrite,
            BeforeBranchEventWrite,
            AfterBranchEventWrite,
            BeforeInstanceFinalizeWrite,
            AfterInstanceFinalizeWrite,
            BeforeReceiptFinalizeWrite,
            AfterReceiptFinalizeWrite,
            BeforeRetryStateWrite,
            AfterRetryStateWrite,
            BeforeRecoveryEventWrite,
            AfterRecoveryEventWrite,
            BeforeLedgerRepairWrite,
            AfterLedgerRepairWrite,
            BeforeCompensationWrite,
            AfterCompensationWrite,
            BeforeCompensationProjection,
            AfterCompensationProjection
        },
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> PauseOnly =
        new HashSet<string>(
            new[] { AfterOutboxClaim },
            StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(
            All.Concat(PauseOnly),
            StringComparer.Ordinal);
}

public interface IDynamicFlowRuntimeFaultInjector
{
    void ThrowIfConfigured(string commandId, int branchOrdinal, string faultPoint);
}

public sealed class DynamicFlowRuntimeInjectedFaultException(string message) : Exception(message);
public sealed class DynamicFlowRuntimeReconcileFenceException(string message) : Exception(message);

public sealed class DynamicFlowRuntimeFaultInjector : IDynamicFlowRuntimeFaultInjector
{
    public const string CommandIdConfigurationKey =
        "DynamicFlowRuntime:TestingFault:CommandId";
    public const string BranchOrdinalConfigurationKey =
        "DynamicFlowRuntime:TestingFault:BranchOrdinal";
    public const string PointsConfigurationKey =
        "DynamicFlowRuntime:TestingFault:Points";
    public const string PauseCommandIdConfigurationKey =
        "DynamicFlowRuntime:TestingPause:CommandId";
    public const string PauseBranchOrdinalConfigurationKey =
        "DynamicFlowRuntime:TestingPause:BranchOrdinal";
    public const string PausePointConfigurationKey =
        "DynamicFlowRuntime:TestingPause:Point";
    public const string PauseReachedFileConfigurationKey =
        "DynamicFlowRuntime:TestingPause:ReachedFile";
    public const string PauseReleaseFileConfigurationKey =
        "DynamicFlowRuntime:TestingPause:ReleaseFile";
    public const string FailureMessage =
        "TEST_ONLY_DYNAMIC_FLOW_RUNTIME_MATERIALIZATION_FAILURE";
    public const string PauseTimeoutMessage =
        "TEST_ONLY_DYNAMIC_FLOW_RUNTIME_PAUSE_TIMEOUT";

    private readonly string? _commandId;
    private readonly int? _branchOrdinal;
    private readonly IReadOnlySet<string> _points;
    private readonly ConcurrentDictionary<string, byte> _consumed = new(StringComparer.Ordinal);
    private readonly string? _pauseCommandId;
    private readonly int? _pauseBranchOrdinal;
    private readonly string? _pausePoint;
    private readonly string? _pauseReachedFile;
    private readonly string? _pauseReleaseFile;
    private readonly ConcurrentDictionary<string, byte> _pauseConsumed =
        new(StringComparer.Ordinal);

    public DynamicFlowRuntimeFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            _points = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        _commandId = Normalize(configuration[CommandIdConfigurationKey]);
        _branchOrdinal = configuration.GetValue<int?>(BranchOrdinalConfigurationKey);
        var configured = ReadPoints(configuration)
            .Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unsupported = configured.Where(value => !DynamicFlowRuntimeFaultPoints.All.Contains(value)).ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unsupported Dynamic Flow runtime test fault point(s): {string.Join(", ", unsupported)}.");
        }

        _points = new HashSet<string>(configured, StringComparer.Ordinal);

        _pauseCommandId = Normalize(configuration[PauseCommandIdConfigurationKey]);
        _pauseBranchOrdinal =
            configuration.GetValue<int?>(PauseBranchOrdinalConfigurationKey);
        _pausePoint = Normalize(configuration[PausePointConfigurationKey])
            ?.ToUpperInvariant();
        _pauseReachedFile = Normalize(
            configuration[PauseReachedFileConfigurationKey]);
        _pauseReleaseFile = Normalize(
            configuration[PauseReleaseFileConfigurationKey]);
        var pauseConfigured =
            _pauseCommandId is not null ||
            _pauseBranchOrdinal.HasValue ||
            _pausePoint is not null ||
            _pauseReachedFile is not null ||
            _pauseReleaseFile is not null;
        if (!pauseConfigured)
            return;
        if (_pauseCommandId is null ||
            _pausePoint is null ||
            _pauseReachedFile is null ||
            _pauseReleaseFile is null)
        {
            throw new InvalidOperationException(
                "Dynamic Flow runtime test pause requires CommandId, Point, ReachedFile, and ReleaseFile.");
        }
        if (!DynamicFlowRuntimeFaultPoints.Known.Contains(_pausePoint))
        {
            throw new InvalidOperationException(
                $"Unsupported Dynamic Flow runtime test pause point: {_pausePoint}.");
        }

        _pauseReachedFile = Path.GetFullPath(_pauseReachedFile);
        _pauseReleaseFile = Path.GetFullPath(_pauseReleaseFile);
        if (string.Equals(
                _pauseReachedFile,
                _pauseReleaseFile,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Dynamic Flow runtime test pause marker paths must be distinct.");
        }
    }

    public void ThrowIfConfigured(string commandId, int branchOrdinal, string faultPoint)
    {
        if (!DynamicFlowRuntimeFaultPoints.Known.Contains(faultPoint))
            throw new ArgumentOutOfRangeException(nameof(faultPoint), faultPoint, "Unknown runtime fault point.");

        PauseIfConfigured(commandId, branchOrdinal, faultPoint);
        if (!DynamicFlowRuntimeFaultPoints.All.Contains(faultPoint))
            return;

        if (!string.Equals(_commandId, Normalize(commandId), StringComparison.Ordinal) ||
            (_branchOrdinal.HasValue && _branchOrdinal.Value != branchOrdinal) ||
            !_points.Contains(faultPoint) ||
            !_consumed.TryAdd($"{commandId}\n{branchOrdinal}\n{faultPoint}", 0))
        {
            return;
        }

        throw new DynamicFlowRuntimeInjectedFaultException(
            $"{FailureMessage}:{faultPoint}:branch={branchOrdinal}");
    }

    private void PauseIfConfigured(
        string commandId,
        int branchOrdinal,
        string faultPoint)
    {
        if (!string.Equals(
                _pauseCommandId,
                Normalize(commandId),
                StringComparison.Ordinal) ||
            (_pauseBranchOrdinal.HasValue &&
             _pauseBranchOrdinal.Value != branchOrdinal) ||
            !string.Equals(_pausePoint, faultPoint, StringComparison.Ordinal) ||
            !_pauseConsumed.TryAdd(
                $"{commandId}\n{branchOrdinal}\n{faultPoint}",
                0))
        {
            return;
        }

        var reachedFile = _pauseReachedFile!;
        var releaseFile = _pauseReleaseFile!;
        var markerDirectory = Path.GetDirectoryName(reachedFile);
        if (!string.IsNullOrWhiteSpace(markerDirectory))
            Directory.CreateDirectory(markerDirectory);

        var temporaryMarker =
            $"{reachedFile}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryMarker,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            {
                var marker = Encoding.UTF8.GetBytes(
                    $"{commandId}\n{branchOrdinal}\n{faultPoint}\n");
                stream.Write(marker);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryMarker, reachedFile);
        }
        finally
        {
            if (File.Exists(temporaryMarker))
                File.Delete(temporaryMarker);
        }

        var timeout = Stopwatch.StartNew();
        while (!File.Exists(releaseFile))
        {
            if (timeout.Elapsed >= TimeSpan.FromSeconds(120))
            {
                throw new DynamicFlowRuntimeInjectedFaultException(
                    $"{PauseTimeoutMessage}:{faultPoint}:branch={branchOrdinal}");
            }
            Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }
    }

    private static IEnumerable<string> ReadPoints(IConfiguration configuration)
    {
        var section = configuration.GetSection(PointsConfigurationKey);
        var children = section.GetChildren()
            .Select(child => Normalize(child.Value))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        if (children.Length > 0)
            return children;
        return (section.Value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(value => value is not null)
            .Cast<string>();
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed class DynamicFlowRuntimeReconcileResult
{
    public string FlowInstanceId { get; init; } = string.Empty;
    public bool Apply { get; init; }
    public string StateBefore { get; init; } = string.Empty;
    public string StateAfter { get; init; } = string.Empty;
    public int ExpectedBranches { get; init; }
    public int AssignmentCount { get; init; }
    public int BindingCount { get; init; }
    public int PeriodCount { get; init; }
    public int QueueCount { get; init; }
    public int AssignmentDocRoleCount { get; init; }
    public int WorkParticipantDocRoleCount { get; init; }
    public int AssignmentReadModelCount { get; init; }
    public int PeriodReadModelCount { get; init; }
    public int CompletedOutboxCount { get; init; }
    public int PendingOutboxCount { get; init; }
    public bool ExactPinsAndRefs { get; init; }
    public bool ExactMaterializationLedgerConverged { get; init; }
    public bool ExactStateProjectionLedgerConverged { get; init; }
    public int StateProjectionDriftCount { get; init; }
    public int StateProjectionAppliedCount { get; init; }
    public IReadOnlyList<string> StateProjectionDriftAssignmentIds { get; init; } =
        Array.Empty<string>();
    public bool ExactLedgerConverged { get; init; }
    public bool Converged { get; init; }
}

public interface IDynamicFlowRuntimeMaterializer
{
    Task<DynamicFlowConfirmResponse> ResumeExistingAsync(
        string receiptId,
        string commandIdentityHash,
        string snapshotToken,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowConfirmResponse> ConfirmAndMaterializeAsync(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowConfirmResponse> ConfirmAndMaterializeSubflowAsync(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request,
        string actorUserId,
        DynamicFlowSubflowLaunchContext context,
        CancellationToken ct = default);

    Task<DynamicFlowConfirmResponse> ConfirmAndMaterializePeriodicAsync(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request,
        string actorUserId,
        DynamicFlowPeriodicLaunchContext context,
        CancellationToken ct = default);

    Task<int> ProcessPendingAsync(int maxItems = 20, CancellationToken ct = default);

    Task<DynamicFlowRuntimeReconcileResult> ReconcileAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        CancellationToken ct = default);

    Task<int> CompensateOwnedArtifactsAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        CancellationToken ct = default);
}

/// <summary>
/// P5 durable runtime implementation. The first transaction only commits owned
/// runtime intent. Assignment/report/read-model side effects are materialized by
/// leased, idempotent outbox items and are therefore observable and repairable.
/// </summary>
public sealed class DynamicFlowRuntimeMaterializer : IDynamicFlowRuntimeMaterializer
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaterializationHeartbeatInterval =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeReconcileLeaseDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RuntimeReconcileHeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IDynamicFlowRuntimePersistence _persistence;
    private readonly IDocRoleService _docRoles;
    private readonly DocRoleReadModelProjectionService _projection;
    private readonly IWorkAssignmentQueueService _queue;
    private readonly IDynamicFlowRuntimeFaultInjector _faults;
    private readonly IDynamicFlowRuntimeActivationPolicy _runtimeActivation;
    private readonly IDynamicFlowRuntimeStateProjector _stateProjector;
    private readonly IWorkReportLifecycleProjectionReconciler _lifecycleReconciler;
    private readonly IWorkReportLifecycleSeriesLockService _lifecycleSeriesLock;
    private readonly IWorkAssignmentProgressService _progress;
    private readonly ILogger<DynamicFlowRuntimeMaterializer> _logger;

    public DynamicFlowRuntimeMaterializer(
        MongoDbContext ctx,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IDynamicFlowRuntimePersistence persistence,
        IDocRoleService docRoles,
        DocRoleReadModelProjectionService projection,
        IWorkAssignmentQueueService queue,
        IDynamicFlowRuntimeFaultInjector faults,
        IDynamicFlowRuntimeActivationPolicy runtimeActivation,
        IDynamicFlowRuntimeStateProjector stateProjector,
        IWorkReportLifecycleProjectionReconciler lifecycleReconciler,
        IWorkReportLifecycleSeriesLockService lifecycleSeriesLock,
        IWorkAssignmentProgressService progress,
        ILogger<DynamicFlowRuntimeMaterializer> logger)
    {
        _ctx = ctx;
        _transactions = transactions;
        _persistence = persistence;
        _docRoles = docRoles;
        _projection = projection;
        _queue = queue;
        _faults = faults;
        _runtimeActivation = runtimeActivation;
        _stateProjector = stateProjector;
        _lifecycleReconciler = lifecycleReconciler;
        _lifecycleSeriesLock = lifecycleSeriesLock;
        _progress = progress;
        _logger = logger;
    }

    public async Task<DynamicFlowConfirmResponse> ResumeExistingAsync(
        string receiptId,
        string commandIdentityHash,
        string snapshotToken,
        string actorUserId,
        CancellationToken ct = default)
    {
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.Id == receiptId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_RECEIPT_NOT_FOUND");
        if (!FixedEquals(receipt.CommandIdentityHash, commandIdentityHash))
            throw new InvalidOperationException("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT");
        if (!FixedEquals(receipt.SnapshotToken, snapshotToken?.Trim()))
            throw new InvalidOperationException("DYNAMIC_FLOW_PREFLIGHT_STALE");
        if (receipt.FlowInstanceId is not null &&
            !await CanExecuteStoredInstanceAsync(receipt.FlowInstanceId, ct))
        {
            return await BuildResponseAsync(
                receipt.Id,
                receipt.SnapshotToken,
                ct);
        }
        await ResumeReceiptAsync(receipt, actorUserId, ct);
        if (receipt.Status == DynamicFlowRuntimeCommandStatuses.Failed &&
            receipt.FlowInstanceId is not null)
        {
            await TryCompensateFailedInstanceAsync(receipt.FlowInstanceId, actorUserId, ct);
        }
        return await BuildResponseAsync(receipt.Id, receipt.SnapshotToken, ct);
    }

    public async Task<DynamicFlowConfirmResponse> ConfirmAndMaterializeAsync(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(request);
        var contractResponse = DynamicFlowRuntimePreflightContract.Confirm(preflight, request);
        if (!string.Equals(
                preflight.Eligibility,
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
                StringComparison.Ordinal) ||
            !CanExecuteRuntimePin(
                preflight.FlowPin.CatalogVersion,
                preflight.FlowPin.CatalogSemanticHash,
                preflight.FlowPin.ArchetypeId))
        {
            return contractResponse;
        }

        var scopeId = LaunchScopeId(preflight.WorkId, preflight.FlowPin.FlowTemplateVersionId);
        var existing = await FindReceiptAsync(scopeId, preflight.CommandId, ct);
        if (existing is not null)
        {
            DynamicFlowRuntimePreflightContract.ResolveReplay(existing, preflight.RequestHash);
            if (!FixedEquals(existing.CommandIdentityHash, preflight.CommandIdentityHash) ||
                !FixedEquals(existing.SnapshotToken, preflight.SnapshotToken))
            {
                throw new InvalidOperationException("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT");
            }
            await ResumeReceiptAsync(existing, actorUserId, ct);
            return await BuildResponseAsync(existing.Id, existing.SnapshotToken, ct);
        }

        var entryFormPin = preflight.FormPins.Single(pin =>
            pin.FormNodeId == preflight.EntryStep.FormNodeId);
        var participantUserIds = preflight.Targets
            .SelectMany(target => target.AssigneeUserIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var activeBindingConflict = await _ctx.WorkTemplateAssignees
            .Find(binding =>
                binding.WorkId == preflight.WorkId &&
                binding.DynamicFormTemplateId == entryFormPin.DynamicFormTemplateId &&
                participantUserIds.Contains(binding.AssigneeUserId) &&
                binding.IsActive &&
                !binding.IsDeleted)
            .AnyAsync(ct);
        if (activeBindingConflict)
            throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_ACTIVE_BINDING_CONFLICT");

        var intent = BuildIntent(preflight, actorUserId);
        _faults.ThrowIfConfigured(
            preflight.CommandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeIntentTransaction);

        try
        {
            await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.BeforeReceiptWrite);
                    await _persistence.InsertReceiptAsync(session, intent.Receipt, transactionCt);
                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.AfterReceiptWrite);

                    var workFilter = Builders<Work>.Filter;
                    var unownedTopology =
                        workFilter.Eq(x => x.AssignmentTopologyOwner, null) |
                        workFilter.Exists(x => x.AssignmentTopologyOwner, false);
                    var noRuntimeOwner =
                        workFilter.Eq(x => x.DynamicFlowRuntimeInstanceId, null) |
                        workFilter.Exists(x => x.DynamicFlowRuntimeInstanceId, false);
                    var existingAssignment = await _ctx.WorkAssignments
                        .Find(
                            session,
                            x => x.WorkId == preflight.WorkId && !x.IsDeleted)
                        .Project(x => x.Id)
                        .AnyAsync(transactionCt);
                    var existingRuntime = await _ctx.DynamicFlowInstances
                        .Find(
                            session,
                            x => x.WorkId == preflight.WorkId && !x.IsDeleted)
                        .Project(x => x.Id)
                        .AnyAsync(transactionCt);
                    if (existingAssignment || existingRuntime)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_RUNTIME_WORK_OWNERSHIP_CONFLICT");
                    }
                    var ownerClaim = await _ctx.Works.UpdateOneAsync(
                        session,
                        workFilter.Eq(x => x.Id, preflight.WorkId) &
                        workFilter.Eq(x => x.IsDeleted, false) &
                        workFilter.Eq(x => x.CompletedAtUtc, null) &
                        workFilter.Ne(x => x.Status, WorkStatus.S3) &
                        unownedTopology &
                        noRuntimeOwner,
                        Builders<Work>.Update
                            .Set(
                                x => x.AssignmentTopologyOwner,
                                WorkAssignmentTopologyOwners.P5FlowRuntime)
                            .Set(
                                x => x.DynamicFlowRuntimeInstanceId,
                                intent.Instance.Id),
                        cancellationToken: transactionCt);
                    if (ownerClaim.MatchedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_RUNTIME_WORK_OWNERSHIP_CONFLICT");
                    }

                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.BeforeInstanceWrite);
                    await _persistence.InsertInstanceAsync(session, intent.Instance, transactionCt);
                    if (intent.Instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId)
                    {
                        await _ctx.DynamicFlowExecutionEpochs.InsertOneAsync(
                            session,
                            new DynamicFlowExecutionEpoch
                            {
                                Id =
                                    DynamicFlowFinalizeTopologyContract
                                        .BuildEpochId(
                                            intent.Instance.Id,
                                            intent.Instance.ExecutionEpoch),
                                FlowInstanceId = intent.Instance.Id,
                                ExecutionEpoch = intent.Instance.ExecutionEpoch,
                                State = DynamicFlowExecutionEpochStates.Active,
                                CheckpointNodeId =
                                    intent.Instance.EntryFlowStepId,
                                IsCanonical = true,
                                OpenedByCommandId =
                                    intent.Instance.LaunchCommandId,
                                OpenedAtUtc = intent.Instance.CreatedAtUtc,
                                Revision = 1,
                                CreatedAtUtc = intent.Instance.CreatedAtUtc,
                                UpdatedAtUtc = intent.Instance.UpdatedAtUtc,
                                CreatedByUserId =
                                    intent.Instance.IssuerUserId,
                                UpdatedByUserId =
                                    intent.Instance.IssuerUserId,
                                IsDeleted = false
                            },
                            cancellationToken: transactionCt);
                    }
                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.AfterInstanceWrite);

                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.BeforeSnapshotWrite);
                    await _persistence.InsertParticipantSnapshotAsync(
                        session,
                        intent.Snapshot,
                        transactionCt);
                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.AfterSnapshotWrite);

                    foreach (var branch in intent.Branches)
                    {
                        _faults.ThrowIfConfigured(
                            preflight.CommandId,
                            branch.Ordinal,
                            DynamicFlowRuntimeFaultPoints.BeforeStepWrite);
                        await _persistence.InsertStepAsync(session, branch.Step, transactionCt);
                        _faults.ThrowIfConfigured(
                            preflight.CommandId,
                            branch.Ordinal,
                            DynamicFlowRuntimeFaultPoints.AfterStepWrite);

                        foreach (var claim in branch.BindingClaims)
                        {
                            _faults.ThrowIfConfigured(
                                preflight.CommandId,
                                branch.Ordinal,
                                DynamicFlowRuntimeFaultPoints.BeforeBindingClaimWrite);
                            await _ctx.WorkTemplateAssignees.InsertOneAsync(
                                session,
                                claim,
                                cancellationToken: transactionCt);
                            _faults.ThrowIfConfigured(
                                preflight.CommandId,
                                branch.Ordinal,
                                DynamicFlowRuntimeFaultPoints.AfterBindingClaimWrite);
                        }
                    }

                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.BeforeEventWrite);
                    await _persistence.AppendEventAsync(session, intent.InitialEvent, transactionCt);
                    _faults.ThrowIfConfigured(
                        preflight.CommandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.AfterEventWrite);

                    foreach (var branch in intent.Branches)
                    {
                        _faults.ThrowIfConfigured(
                            preflight.CommandId,
                            branch.Ordinal,
                            DynamicFlowRuntimeFaultPoints.BeforeOutboxWrite);
                        await _persistence.EnqueueAsync(session, branch.Outbox, transactionCt);
                        _faults.ThrowIfConfigured(
                            preflight.CommandId,
                            branch.Ordinal,
                            DynamicFlowRuntimeFaultPoints.AfterOutboxWrite);
                    }
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        preflight.WorkId,
                        transactionCt);
                },
                ct);
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            existing = await FindReceiptAsync(scopeId, preflight.CommandId, ct);
            if (existing is null)
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_ACTIVE_BINDING_CONFLICT",
                    error);
            DynamicFlowRuntimePreflightContract.ResolveReplay(existing, preflight.RequestHash);
            if (!FixedEquals(existing.CommandIdentityHash, preflight.CommandIdentityHash) ||
                !FixedEquals(existing.SnapshotToken, preflight.SnapshotToken))
            {
                throw new InvalidOperationException("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT");
            }
            await ResumeReceiptAsync(existing, actorUserId, ct);
            return await BuildResponseAsync(existing.Id, existing.SnapshotToken, ct);
        }

        _faults.ThrowIfConfigured(
            preflight.CommandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterIntentCommit);

        await ProcessInstanceAsync(intent.Instance.Id, intent.Branches.Count + 2, ct);
        return await BuildResponseAsync(intent.Receipt.Id, preflight.SnapshotToken, ct);
    }

    public async Task<DynamicFlowConfirmResponse> ConfirmAndMaterializePeriodicAsync(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request,
        string actorUserId,
        DynamicFlowPeriodicLaunchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        _ = DynamicFlowRuntimePreflightContract.Confirm(preflight, request);
        if (preflight.FlowPin.ArchetypeId !=
                DynamicFlowPeriodicTopologyContract.ArchetypeId ||
            preflight.Eligibility !=
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate ||
            !CanExecuteRuntimePin(
                preflight.FlowPin.CatalogVersion,
                preflight.FlowPin.CatalogSemanticHash,
                preflight.FlowPin.ArchetypeId))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_NOT_EXECUTABLE");
        }
        _ = DynamicFlowPeriodicTopologyContract.Require(
            preflight.LockedDefinitionJson,
            preflight.FlowPin.TopologyHash);
        if (preflight.PeriodKey != context.PeriodKey ||
            preflight.ScheduleIdentityJson != context.ScheduleIdentityJson ||
            preflight.ScheduleIdentityHash != context.ScheduleIdentityHash)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_SCHEDULE_PIN_DRIFT");
        }

        var existingInstance = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.PeriodicScheduleId == context.ScheduleId &&
                candidate.PeriodKey == context.PeriodKey &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existingInstance is not null)
        {
            var existingReceipt = await FindReceiptAsync(
                LaunchScopeId(
                    preflight.WorkId,
                    preflight.FlowPin.FlowTemplateVersionId),
                preflight.CommandId,
                ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_RECEIPT_MISSING");
            await ResumeReceiptAsync(existingReceipt, actorUserId, ct);
            return await BuildResponseAsync(
                existingReceipt.Id,
                existingReceipt.SnapshotToken,
                ct);
        }

        var intent = BuildIntent(
            preflight,
            actorUserId,
            periodicContext: context);
        try
        {
            await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var occurrence = await _ctx.DynamicFlowPeriodicOccurrences
                        .Find(session, candidate =>
                            candidate.Id == context.OccurrenceId &&
                            candidate.ScheduleId == context.ScheduleId &&
                            candidate.PeriodKey == context.PeriodKey &&
                            candidate.State ==
                                DynamicFlowPeriodicOccurrenceStates.Launching &&
                            candidate.LeaseId == context.LeaseId &&
                            candidate.Revision ==
                                context.ExpectedOccurrenceRevision &&
                            !candidate.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    if (occurrence is null)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_LEASE_LOST");
                    }

                    var priorInstances = await _ctx.DynamicFlowInstances
                        .Find(session, candidate =>
                            candidate.PeriodicScheduleId == context.ScheduleId &&
                            candidate.Id != intent.Instance.Id &&
                            !candidate.IsDeleted)
                        .Project(candidate => candidate.Id)
                        .ToListAsync(transactionCt);
                    var priorAssignmentIds = priorInstances.Count == 0
                        ? new List<string>()
                        : await _ctx.DynamicFlowStepInstances
                            .Find(session, candidate =>
                                priorInstances.Contains(candidate.FlowInstanceId) &&
                                candidate.AssignmentId != null &&
                                !candidate.IsDeleted)
                            .Project(candidate => candidate.AssignmentId!)
                            .ToListAsync(transactionCt);
                    if (priorAssignmentIds.Count > 0)
                    {
                        await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                            session,
                            candidate =>
                                priorAssignmentIds.Contains(
                                    candidate.WorkAssignmentId) &&
                                candidate.IsActive &&
                                !candidate.IsDeleted,
                            Builders<WorkTemplateAssignee>.Update
                                .Set(candidate => candidate.IsActive, false)
                                .Set(candidate => candidate.UpdatedAtUtc, DateTime.UtcNow)
                                .Set(candidate => candidate.UpdatedByUserId, actorUserId),
                            cancellationToken: transactionCt);
                    }

                    var workFilter = Builders<Work>.Filter;
                    var allowedOwner =
                        workFilter.Eq(x => x.AssignmentTopologyOwner, null) |
                        workFilter.Exists(x => x.AssignmentTopologyOwner, false) |
                        workFilter.Eq(
                            x => x.AssignmentTopologyOwner,
                            WorkAssignmentTopologyOwners.P5FlowRuntime);
                    var workUpdate = await _ctx.Works.UpdateOneAsync(
                        session,
                        workFilter.Eq(x => x.Id, preflight.WorkId) &
                        workFilter.Eq(x => x.IsDeleted, false) &
                        workFilter.Eq(x => x.CompletedAtUtc, null) &
                        workFilter.Ne(x => x.Status, WorkStatus.S3) &
                        allowedOwner,
                        Builders<Work>.Update
                            .Set(
                                x => x.AssignmentTopologyOwner,
                                WorkAssignmentTopologyOwners.P5FlowRuntime)
                            .Set(
                                x => x.DynamicFlowRuntimeInstanceId,
                                intent.Instance.Id),
                        cancellationToken: transactionCt);
                    if (workUpdate.MatchedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_PERIODIC_WORK_OWNERSHIP_CONFLICT");
                    }

                    await _persistence.InsertReceiptAsync(
                        session,
                        intent.Receipt,
                        transactionCt);
                    await _persistence.InsertInstanceAsync(
                        session,
                        intent.Instance,
                        transactionCt);
                    await _persistence.InsertParticipantSnapshotAsync(
                        session,
                        intent.Snapshot,
                        transactionCt);
                    foreach (var branch in intent.Branches)
                    {
                        await _persistence.InsertStepAsync(
                            session,
                            branch.Step,
                            transactionCt);
                        foreach (var claim in branch.BindingClaims)
                        {
                            await _ctx.WorkTemplateAssignees.InsertOneAsync(
                                session,
                                claim,
                                cancellationToken: transactionCt);
                        }
                    }
                    await _persistence.AppendEventAsync(
                        session,
                        intent.InitialEvent,
                        transactionCt);
                    foreach (var branch in intent.Branches)
                    {
                        await _persistence.EnqueueAsync(
                            session,
                            branch.Outbox,
                            transactionCt);
                    }

                    var occurrenceUpdate =
                        await _ctx.DynamicFlowPeriodicOccurrences.UpdateOneAsync(
                            session,
                            candidate =>
                                candidate.Id == context.OccurrenceId &&
                                candidate.State ==
                                    DynamicFlowPeriodicOccurrenceStates.Launching &&
                                candidate.LeaseId == context.LeaseId &&
                                candidate.Revision ==
                                    context.ExpectedOccurrenceRevision,
                            Builders<DynamicFlowPeriodicOccurrence>.Update
                                .Set(
                                    candidate => candidate.State,
                                    DynamicFlowPeriodicOccurrenceStates.Launched)
                                .Set(
                                    candidate => candidate.FlowInstanceId,
                                    intent.Instance.Id)
                                .Set(candidate => candidate.ReasonCode, null)
                                .Set(candidate => candidate.LeaseId, null)
                                .Set(candidate => candidate.LeaseUntilUtc, null)
                                .Set(candidate => candidate.UpdatedAtUtc, DateTime.UtcNow)
                                .Set(candidate => candidate.UpdatedByUserId, actorUserId)
                                .Inc(candidate => candidate.Revision, 1),
                            cancellationToken: transactionCt);
                    if (occurrenceUpdate.ModifiedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_LEASE_LOST");
                    }
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        preflight.WorkId,
                        transactionCt);
                },
                ct);
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            var replayInstance = await _ctx.DynamicFlowInstances
                .Find(candidate =>
                    candidate.PeriodicScheduleId == context.ScheduleId &&
                    candidate.PeriodKey == context.PeriodKey &&
                    !candidate.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (replayInstance is null)
                throw;
            var replayReceipt = await FindReceiptAsync(
                LaunchScopeId(
                    preflight.WorkId,
                    preflight.FlowPin.FlowTemplateVersionId),
                preflight.CommandId,
                ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_RECEIPT_MISSING",
                    error);
            await ResumeReceiptAsync(replayReceipt, actorUserId, ct);
            return await BuildResponseAsync(
                replayReceipt.Id,
                replayReceipt.SnapshotToken,
                ct);
        }

        await ProcessInstanceAsync(
            intent.Instance.Id,
            intent.Branches.Count + 2,
            ct);
        return await BuildResponseAsync(
            intent.Receipt.Id,
            preflight.SnapshotToken,
            ct);
    }

    public async Task<DynamicFlowConfirmResponse> ConfirmAndMaterializeSubflowAsync(
        DynamicFlowPreflightResponse preflight,
        DynamicFlowConfirmRequest request,
        string actorUserId,
        DynamicFlowSubflowLaunchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        _ = DynamicFlowRuntimePreflightContract.Confirm(preflight, request);
        if (!string.Equals(
                preflight.Eligibility,
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
                StringComparison.Ordinal) ||
            !CanExecuteRuntimePin(
                preflight.FlowPin.CatalogVersion,
                preflight.FlowPin.CatalogSemanticHash,
                preflight.FlowPin.ArchetypeId))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_SUBFLOW_CHILD_NOT_EXECUTABLE");
        }

        var scopeId = LaunchScopeId(
            preflight.WorkId,
            preflight.FlowPin.FlowTemplateVersionId);
        var existing = await FindReceiptAsync(
            scopeId,
            preflight.CommandId,
            ct);
        if (existing is not null)
        {
            DynamicFlowRuntimePreflightContract.ResolveReplay(
                existing,
                preflight.RequestHash);
            if (!FixedEquals(
                    existing.CommandIdentityHash,
                    preflight.CommandIdentityHash) ||
                !FixedEquals(
                    existing.SnapshotToken,
                    preflight.SnapshotToken))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT");
            }
            await ResumeReceiptAsync(existing, actorUserId, ct);
            if (existing.FlowInstanceId is not null &&
                await CanExecuteStoredInstanceAsync(
                    existing.FlowInstanceId,
                    ct))
            {
                await _stateProjector.ProjectSubflowParentTerminalAsync(
                    existing.FlowInstanceId,
                    actorUserId,
                    ct);
            }
            return await BuildResponseAsync(
                existing.Id,
                existing.SnapshotToken,
                ct);
        }

        var intent = BuildIntent(preflight, actorUserId, context);
        var parentEventId =
            DynamicFlowSubflowTopologyContract.BuildParentEventId(
                context.ParentInstanceId,
                preflight.CommandId,
                DynamicFlowSubflowTopologyContract.ChildLaunchedEvent);
        var now = DateTime.UtcNow;
        try
        {
            await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await _ctx.DynamicFlowRuntimeCommandReceipts
                        .Find(session, candidate =>
                            candidate.Id == intent.Receipt.Id)
                        .FirstOrDefaultAsync(transactionCt);
                    if (replay is not null)
                    {
                        DynamicFlowRuntimePreflightContract.ResolveReplay(
                            replay,
                            preflight.RequestHash);
                        return;
                    }

                    var parent = await _ctx.DynamicFlowInstances
                        .Find(session, candidate =>
                            candidate.Id == context.ParentInstanceId &&
                            candidate.WorkId == preflight.WorkId &&
                            candidate.ArchetypeId ==
                                DynamicFlowSubflowTopologyContract.ArchetypeId &&
                            candidate.State ==
                                DynamicFlowInstanceStates.Active &&
                            candidate.Revision ==
                                context.ExpectedParentInstanceRevision &&
                            !candidate.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    var parentStep = await _ctx.DynamicFlowStepInstances
                        .Find(session, candidate =>
                            candidate.Id == context.ParentStepInstanceId &&
                            candidate.FlowInstanceId ==
                                context.ParentInstanceId &&
                            candidate.State ==
                                DynamicFlowStepStates.Approved &&
                            candidate.Revision ==
                                context.ExpectedParentStepRevision &&
                            candidate.ChildInstanceId == null &&
                            !candidate.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    if (parent is null || parentStep is null)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_SUBFLOW_PARENT_REVISION_CONFLICT");
                    }

                    var parentTopology =
                        DynamicFlowSubflowTopologyContract.Require(
                            parent.TopologySnapshotJson,
                            parent.TopologySnapshotHash);
                    if (parentTopology.ChildFlowFamilyId !=
                            preflight.FlowPin.FlowTemplateId ||
                        parentTopology.ChildFlowVersionId !=
                            preflight.FlowPin.FlowTemplateVersionId ||
                        context.ParentEventSequence !=
                            parent.NextEventSequence)
                    {
                        throw new InvalidOperationException(
                            DynamicFlowSubflowTopologyContract.ChildPinDrift);
                    }

                    await _persistence.InsertReceiptAsync(
                        session,
                        intent.Receipt,
                        transactionCt);
                    await _persistence.InsertInstanceAsync(
                        session,
                        intent.Instance,
                        transactionCt);
                    await _persistence.InsertParticipantSnapshotAsync(
                        session,
                        intent.Snapshot,
                        transactionCt);
                    foreach (var branch in intent.Branches)
                    {
                        await _persistence.InsertStepAsync(
                            session,
                            branch.Step,
                            transactionCt);
                        foreach (var claim in branch.BindingClaims)
                        {
                            await _ctx.WorkTemplateAssignees.InsertOneAsync(
                                session,
                                claim,
                                cancellationToken: transactionCt);
                        }
                    }
                    await _persistence.AppendEventAsync(
                        session,
                        intent.InitialEvent,
                        transactionCt);
                    foreach (var branch in intent.Branches)
                    {
                        await _persistence.EnqueueAsync(
                            session,
                            branch.Outbox,
                            transactionCt);
                    }

                    var parentStepUpdate =
                        await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            candidate =>
                                candidate.Id == parentStep.Id &&
                                candidate.FlowInstanceId == parent.Id &&
                                candidate.State ==
                                    DynamicFlowStepStates.Approved &&
                                candidate.Revision ==
                                    context.ExpectedParentStepRevision &&
                                candidate.ChildInstanceId == null &&
                                !candidate.IsDeleted,
                            Builders<DynamicFlowStepInstance>.Update
                                .Set(
                                    candidate => candidate.State,
                                    DynamicFlowStepStates.WaitingChild)
                                .Set(
                                    candidate => candidate.ChildInstanceId,
                                    intent.Instance.Id)
                                .Set(
                                    candidate =>
                                        candidate.ChildFlowTemplateId,
                                    intent.Instance.FlowTemplateId)
                                .Set(
                                    candidate =>
                                        candidate.ChildFlowVersionId,
                                    intent.Instance.FlowTemplateVersionId)
                                .Set(
                                    candidate => candidate.ChildState,
                                    DynamicFlowInstanceStates.Materializing)
                                .Set(
                                    candidate =>
                                        candidate.ChildOutcomeCode,
                                    null)
                                .Set(
                                    candidate =>
                                        candidate.ChildLinkedAtUtc,
                                    now)
                                .Set(
                                    candidate => candidate.UpdatedAtUtc,
                                    now)
                                .Set(
                                    candidate => candidate.UpdatedByUserId,
                                    actorUserId)
                                .Inc(candidate => candidate.Revision, 1),
                            cancellationToken: transactionCt);
                    if (parentStepUpdate.ModifiedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_SUBFLOW_PARENT_REVISION_CONFLICT");
                    }

                    var parentUpdate =
                        await _ctx.DynamicFlowInstances.UpdateOneAsync(
                            session,
                            candidate =>
                                candidate.Id == parent.Id &&
                                candidate.State ==
                                    DynamicFlowInstanceStates.Active &&
                                candidate.Revision ==
                                    context.ExpectedParentInstanceRevision &&
                                candidate.NextEventSequence ==
                                    context.ParentEventSequence &&
                                !candidate.IsDeleted,
                            Builders<DynamicFlowInstance>.Update
                                .Set(
                                    candidate => candidate.UpdatedAtUtc,
                                    now)
                                .Set(
                                    candidate => candidate.UpdatedByUserId,
                                    actorUserId)
                                .Inc(candidate => candidate.Revision, 1)
                                .Inc(
                                    candidate =>
                                        candidate.NextEventSequence,
                                    1),
                            cancellationToken: transactionCt);
                    if (parentUpdate.ModifiedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_SUBFLOW_PARENT_REVISION_CONFLICT");
                    }

                    var parentPayload = new BsonDocument
                    {
                        { "parentInstanceId", parent.Id },
                        { "parentStepInstanceId", parentStep.Id },
                        { "childInstanceId", intent.Instance.Id },
                        {
                            "childFlowTemplateId",
                            intent.Instance.FlowTemplateId
                        },
                        {
                            "childFlowVersionId",
                            intent.Instance.FlowTemplateVersionId
                        },
                        {
                            "ancestryDepth",
                            intent.Instance.AncestryPath.Count
                        }
                    };
                    await _persistence.AppendEventAsync(
                        session,
                        new DynamicFlowRuntimeEvent
                        {
                            Id = parentEventId,
                            FlowInstanceId = parent.Id,
                            StepInstanceId = parentStep.Id,
                            ExecutionEpoch = parent.ExecutionEpoch,
                            BranchId = parentStep.BranchId,
                            AttemptNo = parentStep.AttemptNo,
                            Sequence = context.ParentEventSequence,
                            EventType =
                                DynamicFlowSubflowTopologyContract
                                    .ChildLaunchedEvent,
                            CommandId = preflight.CommandId,
                            CorrelationId = preflight.CommandId,
                            FromState = DynamicFlowStepStates.Approved,
                            ToState =
                                DynamicFlowStepStates.WaitingChild,
                            FromRevision = parentStep.Revision,
                            ToRevision = parentStep.Revision + 1,
                            ReasonCode = "EXACT_PINNED_CHILD_LAUNCHED",
                            AffectedRefs =
                            [
                                $"instance:{parent.Id}",
                                $"step:{parentStep.Id}",
                                $"instance:{intent.Instance.Id}"
                            ],
                            ActorUserId = actorUserId,
                            VisibleUnitIds = parentStep.ParticipantUserIds
                                .Select(userId => parentStep.TargetUnitId)
                                .Append(parent.IssuerUnitId)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToList(),
                            Payload = parentPayload,
                            PayloadHash = Hash(parentPayload.ToJson()),
                            OccurredAtUtc = now
                        },
                        transactionCt);
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        preflight.WorkId,
                        transactionCt);
                },
                ct);
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            existing = await FindReceiptAsync(
                scopeId,
                preflight.CommandId,
                ct);
            if (existing is null)
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_SUBFLOW_ACTIVE_BINDING_CONFLICT",
                    error);
            }
            DynamicFlowRuntimePreflightContract.ResolveReplay(
                existing,
                preflight.RequestHash);
            await ResumeReceiptAsync(existing, actorUserId, ct);
            if (existing.FlowInstanceId is not null &&
                await CanExecuteStoredInstanceAsync(
                    existing.FlowInstanceId,
                    ct))
            {
                await _stateProjector.ProjectSubflowParentTerminalAsync(
                    existing.FlowInstanceId,
                    actorUserId,
                    ct);
            }
            return await BuildResponseAsync(
                existing.Id,
                existing.SnapshotToken,
                ct);
        }

        await ProcessInstanceAsync(
            intent.Instance.Id,
            intent.Branches.Count + 2,
            ct);
        await _stateProjector.ProjectSubflowParentTerminalAsync(
            intent.Instance.Id,
            actorUserId,
            ct);
        return await BuildResponseAsync(
            intent.Receipt.Id,
            preflight.SnapshotToken,
            ct);
    }

    private async Task ResumeReceiptAsync(
        DynamicFlowRuntimeCommandReceipt receipt,
        string actorUserId,
        CancellationToken ct)
    {
        if (receipt.Status != DynamicFlowRuntimeCommandStatuses.Pending)
            return;
        var flowInstanceId = receipt.FlowInstanceId
                             ?? throw new InvalidOperationException(
                                 "DYNAMIC_FLOW_RUNTIME_RECEIPT_INSTANCE_MISSING");
        if (!await CanExecuteStoredInstanceAsync(flowInstanceId, ct))
            return;
        var recovery = await PreparePendingReplayAsync(flowInstanceId, actorUserId, ct);
        await ProcessInstanceAsync(flowInstanceId, MaxAttempts * 4, ct);
        if (!recovery)
            return;
        var repaired = await InspectReconcileAsync(
            flowInstanceId,
            true,
            DynamicFlowInstanceStates.Partial,
            ct);
        if (repaired.Converged)
        {
            await AppendRecoveryEventAsync(
                flowInstanceId,
                DynamicFlowRuntimeEventTypes.RecoveryCompleted,
                actorUserId,
                ct);
        }
    }

    public async Task<int> ProcessPendingAsync(int maxItems = 20, CancellationToken ct = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 200);
        var processed = 0;
        var processedInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < maxItems; index++)
        {
            var item = await ClaimNextAsync(null, ct);
            if (item is null)
                break;
            await ProcessClaimedAsync(item, ct);
            processedInstanceIds.Add(item.FlowInstanceId);
            processed++;
        }

        foreach (var instanceId in processedInstanceIds)
        {
            await TryFinalizeInstanceAsync(instanceId, ct);
            var issuerUserId = await _ctx.DynamicFlowInstances
                .Find(candidate =>
                    candidate.Id == instanceId &&
                    !candidate.IsDeleted)
                .Project(candidate => candidate.IssuerUserId)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(issuerUserId))
            {
                await _stateProjector
                    .ProjectSubflowParentTerminalAsync(
                        instanceId,
                        issuerUserId,
                        ct);
            }
        }
        await FinalizeReadyInstancesAsync(maxItems, ct);
        await CompensateReadyFailedInstancesAsync(maxItems, ct);
        return processed;
    }

    private async Task FinalizeReadyInstancesAsync(int maxItems, CancellationToken ct)
    {
        var instanceIds = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x =>
                x.Status == DynamicFlowRuntimeCommandStatuses.Pending &&
                x.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                x.CommandType == "LAUNCH" &&
                x.FlowInstanceId != null)
            .SortBy(x => x.CreatedAtUtc)
            .Limit(maxItems)
            .Project(x => x.FlowInstanceId!)
            .ToListAsync(ct);
        foreach (var instanceId in instanceIds.Distinct(StringComparer.Ordinal))
        {
            if (await CanExecuteStoredInstanceAsync(instanceId, ct))
                await TryFinalizeInstanceAsync(instanceId, ct);
        }
    }

    private async Task CompensateReadyFailedInstancesAsync(int maxItems, CancellationToken ct)
    {
        var failed = await _ctx.DynamicFlowInstances
            .Find(x =>
                x.State == DynamicFlowInstanceStates.Failed &&
                x.CompensatedAtUtc == null &&
                !x.IsDeleted)
            .SortBy(x => x.UpdatedAtUtc)
            .Limit(maxItems)
            .Project(x => new { x.Id, x.IssuerUserId })
            .ToListAsync(ct);
        var remaining = maxItems - failed.Count;
        if (remaining > 0)
        {
            var compensated = await _ctx.DynamicFlowInstances
                .Find(x =>
                    x.State == DynamicFlowInstanceStates.Failed &&
                    x.CompensatedAtUtc != null &&
                    !x.IsDeleted)
                .SortBy(x => x.CompensationCheckedAtUtc)
                .ThenBy(x => x.CompensatedAtUtc)
                .ThenBy(x => x.Id)
                .Limit(remaining)
                .Project(x => new { x.Id, x.IssuerUserId })
                .ToListAsync(ct);
            failed.AddRange(compensated);
        }
        foreach (var instance in failed)
        {
            if (await CanExecuteStoredInstanceAsync(instance.Id, ct))
            {
                await TryCompensateFailedInstanceAsync(
                    instance.Id,
                    instance.IssuerUserId,
                    ct);
            }
        }
    }

    public async Task<DynamicFlowRuntimeReconcileResult> ReconcileAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(flowInstanceId, out _))
            throw new ArgumentException("DYNAMIC_FLOW_INSTANCE_ID_INVALID", nameof(flowInstanceId));
        if (!ObjectId.TryParse(actorUserId, out _))
            throw new ArgumentException("DYNAMIC_FLOW_RECONCILE_ACTOR_REQUIRED", nameof(actorUserId));

        string? runtimeReconcileLeaseId = null;
        var reconcileLeases = new List<WorkReportLifecycleSeriesLease>();
        CancellationTokenSource? reconcileOperationCancellation = null;
        CancellationTokenSource? heartbeatCancellation = null;
        Task? heartbeatTask = null;
        var heartbeatFailure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (apply)
        {
            runtimeReconcileLeaseId = await AcquireRuntimeInstanceReconcileLeaseAsync(
                flowInstanceId,
                ct);
            try
            {
                reconcileLeases = await AcquireRuntimeReconcileLeasesAsync(
                    flowInstanceId,
                    ct);
                reconcileOperationCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeatCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        reconcileOperationCancellation.Token);
                heartbeatTask = RunRuntimeReconcileHeartbeatAsync(
                    flowInstanceId,
                    runtimeReconcileLeaseId,
                    reconcileLeases,
                    heartbeatCancellation,
                    reconcileOperationCancellation,
                    heartbeatFailure);
                ct = reconcileOperationCancellation.Token;
            }
            catch
            {
                await ReleaseRuntimeInstanceReconcileLeaseAsync(
                    flowInstanceId,
                    runtimeReconcileLeaseId);
                throw;
            }
        }
        try
        {
            if (apply)
            {
                await RenewRuntimeInstanceReconcileLeaseAsync(
                    flowInstanceId,
                    runtimeReconcileLeaseId!,
                    ct);
            }
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            var before = await _ctx.DynamicFlowInstances
                .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
            var preview = await InspectReconcileAsync(flowInstanceId, apply, before.State, ct);
            if (apply && await HasLiveMaterializationClaimAsync(flowInstanceId, ct))
                return preview;
            DynamicFlowRuntimeStateReconcileResult? lifecycleState = null;
            if (apply)
            {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            // P3 lifecycle debt can itself make the materialization ledger look
            // inexact (period provenance, queue and read models). Drain that
            // durable debt before deciding that P5 materialization must enter
            // PARTIAL/RETRYING recovery.
            lifecycleState = await DrainReportLifecycleDebtAsync(
                flowInstanceId,
                apply,
                reconcileLeases,
                ct);
            preview = await InspectReconcileAsync(
                flowInstanceId,
                apply,
                before.State,
                ct);
            if (lifecycleState.DriftCount > 0)
            {
                // Another P3 worker may own the durable report claim. Do not
                // misclassify that normal in-flight debt as a P5 materialization
                // failure or mutate the instance into PARTIAL/RETRYING.
                return MergeStateReconcile(preview, lifecycleState);
            }
            }
            DynamicFlowRuntimeStateReconcileResult? statePreview = null;
            if (preview.ExactMaterializationLedgerConverged)
            {
                statePreview = await _stateProjector.ReconcileInstanceAsync(
                    flowInstanceId,
                    apply: false,
                    actorUserId,
                    ct);
            }
            var combinedPreview = MergeStateReconcile(preview, statePreview);
            if (!apply || combinedPreview.Converged)
                return combinedPreview;
            if (!preview.ExactMaterializationLedgerConverged &&
                before.State is
                    DynamicFlowInstanceStates.Active or
                    DynamicFlowInstanceStates.Completed)
            {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await RepairOwnedMaterializationArtifactsAsync(
                before,
                actorUserId,
                reconcileLeases,
                runtimeReconcileLeaseId!,
                ct);
            reconcileLeases = await RefreshRuntimeReconcileLeasesAsync(
                flowInstanceId,
                reconcileLeases,
                ct);
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await RepairProjectionReadModelsAsync(
                before,
                actorUserId,
                reconcileLeases,
                runtimeReconcileLeaseId!,
                ct);
            preview = await InspectReconcileAsync(
                flowInstanceId,
                apply,
                before.State,
                ct);
            if (preview.ExactMaterializationLedgerConverged)
            {
                statePreview = await _stateProjector.ReconcileInstanceAsync(
                    flowInstanceId,
                    apply: false,
                    actorUserId,
                    ct);
                combinedPreview = MergeStateReconcile(preview, statePreview);
                if (combinedPreview.Converged)
                    return combinedPreview;
            }
            }
            if (preview.ExactMaterializationLedgerConverged &&
                !preview.ExactStateProjectionLedgerConverged)
            {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            if (await RepairRuntimeEventCursorAsync(
                    flowInstanceId,
                    reconcileLeases,
                    runtimeReconcileLeaseId!,
                    ct))
            {
                preview = await InspectReconcileAsync(
                    flowInstanceId,
                    apply,
                    before.State,
                    ct);
                statePreview = await _stateProjector.ReconcileInstanceAsync(
                    flowInstanceId,
                    apply: false,
                    actorUserId,
                    ct);
                combinedPreview = MergeStateReconcile(preview, statePreview);
                if (combinedPreview.Converged)
                    return combinedPreview;
            }
            }
            if (!preview.ExactMaterializationLedgerConverged &&
                before.State is DynamicFlowInstanceStates.Failed
                    or DynamicFlowInstanceStates.Completed
                    or DynamicFlowInstanceStates.Terminated)
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_RECONCILE_TERMINAL_INSTANCE");
            }

            if (!preview.ExactMaterializationLedgerConverged)
            {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await PrepareLedgerRepairAsync(
                flowInstanceId,
                actorUserId,
                reconcileLeases,
                runtimeReconcileLeaseId!,
                ct);

            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await EnsureRetryingAsync(
                flowInstanceId,
                actorUserId,
                ct,
                runtimeReconcileLeaseId);
            await AppendRecoveryEventAsync(
                flowInstanceId,
                DynamicFlowRuntimeEventTypes.RecoveryStarted,
                actorUserId,
                ct,
                runtimeReconcileLeaseId);
            await ProcessInstanceAsync(
                flowInstanceId,
                MaxAttempts * 4,
                ct,
                reconcileLeases,
                runtimeReconcileLeaseId);
            if (heartbeatCancellation is not null)
            {
                heartbeatCancellation.Cancel();
                if (heartbeatTask is not null)
                {
                    try
                    {
                        await heartbeatTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected while replacing the heartbeat lease snapshot.
                    }
                }
                heartbeatCancellation.Dispose();
                heartbeatCancellation = null;
                heartbeatTask = null;
            }
            await RenewRuntimeInstanceReconcileLeaseAsync(
                flowInstanceId,
                runtimeReconcileLeaseId!,
                ct);
            reconcileLeases = await RefreshRuntimeReconcileLeasesAsync(
                flowInstanceId,
                reconcileLeases,
                ct);
            heartbeatCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    reconcileOperationCancellation!.Token);
            heartbeatTask = RunRuntimeReconcileHeartbeatAsync(
                flowInstanceId,
                runtimeReconcileLeaseId!,
                reconcileLeases,
                heartbeatCancellation,
                reconcileOperationCancellation,
                heartbeatFailure);
            await TryFinalizeInstanceAsync(
                flowInstanceId,
                ct,
                runtimeReconcileLeaseId);
            }

            var materialized = await InspectReconcileAsync(
                flowInstanceId,
                apply,
                before.State,
                ct);
            var stateAppliedCount = 0;
            if (materialized.ExactMaterializationLedgerConverged)
            {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            var pendingState = await _stateProjector.ReconcileInstanceAsync(
                flowInstanceId,
                apply: false,
                actorUserId,
                ct);
            foreach (var reportId in pendingState.PendingReportIds)
            {
                await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
                await _lifecycleReconciler.ReconcileReportAsync(reportId, ct);
            }
            var lifecycleDebt = await _stateProjector.ReconcileInstanceAsync(
                flowInstanceId,
                apply: false,
                actorUserId,
                ct);
            foreach (var reportId in lifecycleDebt.UnprojectedReportIds)
            {
                await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
                await RepairUnprojectedReportLifecycleAsync(
                    flowInstanceId,
                    reportId,
                    ct);
            }

            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            var stateApplied = await _stateProjector.ReconcileInstanceAsync(
                flowInstanceId,
                apply: true,
                actorUserId,
                ct);
            stateAppliedCount = stateApplied.ProjectedCount;
            }
            var repaired = await InspectReconcileAsync(flowInstanceId, apply, before.State, ct);
            var stateFinal = repaired.ExactMaterializationLedgerConverged
                ? await _stateProjector.ReconcileInstanceAsync(
                    flowInstanceId,
                    apply: false,
                    actorUserId,
                    ct)
                : null;
            var combined = MergeStateReconcile(repaired, stateFinal, stateAppliedCount);
            if (combined.Converged &&
                !preview.ExactMaterializationLedgerConverged)
            {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await AppendRecoveryEventAsync(
                flowInstanceId,
                DynamicFlowRuntimeEventTypes.RecoveryCompleted,
                actorUserId,
                ct,
                runtimeReconcileLeaseId);
            var afterRecoveryEvent = await InspectReconcileAsync(
                flowInstanceId,
                apply,
                before.State,
                ct);
            combined = MergeStateReconcile(
                afterRecoveryEvent,
                await _stateProjector.ReconcileInstanceAsync(
                    flowInstanceId,
                    apply: false,
                    actorUserId,
                    ct),
                stateAppliedCount);
            }

            return combined;
        }
        catch (OperationCanceledException) when (
            heartbeatFailure.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_RECONCILE_LEASE_HEARTBEAT_FAILED",
                await heartbeatFailure.Task);
        }
        finally
        {
            if (heartbeatCancellation is not null)
            {
                heartbeatCancellation.Cancel();
                if (heartbeatTask is not null)
                {
                    try
                    {
                        await heartbeatTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the reconcile scope ends normally.
                    }
                }
                heartbeatCancellation.Dispose();
            }
            reconcileOperationCancellation?.Cancel();
            reconcileOperationCancellation?.Dispose();
            for (var index = reconcileLeases.Count - 1; index >= 0; index--)
                await reconcileLeases[index].DisposeAsync();
            if (runtimeReconcileLeaseId is not null)
            {
                await ReleaseRuntimeInstanceReconcileLeaseAsync(
                    flowInstanceId,
                    runtimeReconcileLeaseId);
            }
        }
    }

    private async Task<List<WorkReportLifecycleSeriesLease>>
        AcquireRuntimeReconcileLeasesAsync(
            string flowInstanceId,
            CancellationToken ct)
    {
        const int maxAcquireAttempts = 50;
        for (var attempt = 1; attempt <= maxAcquireAttempts; attempt++)
        {
            var assignmentIds =
                await LoadRuntimeReconcileAssignmentIdsAsync(
                    flowInstanceId,
                    ct);
            var leases = new List<WorkReportLifecycleSeriesLease>(
                assignmentIds.Count);
            try
            {
                foreach (var assignmentId in assignmentIds)
                {
                    leases.Add(await _lifecycleSeriesLock.AcquireAsync(
                        assignmentId,
                        WorkReportLifecycleSeriesOperations.RuntimeReconcile,
                        ct));
                }
                var observedAssignmentIds =
                    await LoadRuntimeReconcileAssignmentIdsAsync(
                        flowInstanceId,
                        ct);
                if (!assignmentIds.SequenceEqual(
                        observedAssignmentIds,
                        StringComparer.Ordinal))
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_SET_CHANGED");
                }
                await RenewRuntimeReconcileLeasesAsync(leases, ct);
                return leases;
            }
            catch (AppException error) when (
                error.Code ==
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT &&
                attempt < maxAcquireAttempts)
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                    await leases[index].DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
            catch (DynamicFlowRuntimeReconcileFenceException) when (
                attempt < maxAcquireAttempts)
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                    await leases[index].DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
            catch
            {
                for (var index = leases.Count - 1; index >= 0; index--)
                    await leases[index].DisposeAsync();
                throw;
            }
        }

        throw new InvalidOperationException(
            "DYNAMIC_FLOW_RUNTIME_RECONCILE_LEASE_RETRY_EXHAUSTED");
    }

    private async Task<List<string>> LoadRuntimeReconcileAssignmentIdsAsync(
        string flowInstanceId,
        CancellationToken ct)
    {
        var referencedIds = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                !x.IsDeleted &&
                x.AssignmentId != null)
            .Project(x => x.AssignmentId!)
            .ToListAsync(ct);
        referencedIds = referencedIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var ownedIds = await _ctx.WorkAssignments
            .Find(x =>
                !x.IsDeleted &&
                (x.FlowInstanceId == flowInstanceId ||
                 referencedIds.Contains(x.Id)))
            .Project(x => x.Id)
            .ToListAsync(ct);
        return ownedIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task RenewRuntimeReconcileLeasesAsync(
        IReadOnlyList<WorkReportLifecycleSeriesLease> leases,
        CancellationToken ct)
    {
        foreach (var lease in leases)
            await lease.RenewAsync(ct);
    }

    private async Task<List<WorkReportLifecycleSeriesLease>>
        RefreshRuntimeReconcileLeasesAsync(
            string flowInstanceId,
            IReadOnlyList<WorkReportLifecycleSeriesLease> currentLeases,
            CancellationToken ct)
    {
        const int maxAcquireAttempts = 50;
        for (var attempt = 1; attempt <= maxAcquireAttempts; attempt++)
        {
            var assignmentIds =
                (await LoadRuntimeReconcileAssignmentIdsAsync(
                    flowInstanceId,
                    ct)).ToArray();
            var existingByAssignmentId = currentLeases.ToDictionary(
                lease => lease.AssignmentId,
                StringComparer.Ordinal);
            var acquired = new List<WorkReportLifecycleSeriesLease>();
            try
            {
                foreach (var assignmentId in assignmentIds)
                {
                    if (existingByAssignmentId.ContainsKey(assignmentId))
                        continue;
                    acquired.Add(await _lifecycleSeriesLock.AcquireAsync(
                        assignmentId,
                        WorkReportLifecycleSeriesOperations.RuntimeReconcile,
                        ct));
                }

                var observedIds =
                    (await LoadRuntimeReconcileAssignmentIdsAsync(
                        flowInstanceId,
                        ct)).ToArray();
                if (!assignmentIds.SequenceEqual(
                        observedIds,
                        StringComparer.Ordinal))
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_SET_CHANGED");
                }

                var refreshed = currentLeases
                    .Concat(acquired)
                    .OrderBy(lease => lease.AssignmentId, StringComparer.Ordinal)
                    .ToList();
                if (!refreshed
                        .Select(lease => lease.AssignmentId)
                        .SequenceEqual(assignmentIds, StringComparer.Ordinal))
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_SET_CHANGED");
                }
                await RenewRuntimeReconcileLeasesAsync(refreshed, ct);
                return refreshed;
            }
            catch (Exception error) when (
                (error is DynamicFlowRuntimeReconcileFenceException ||
                 error is AppException appError &&
                 appError.Code ==
                 AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT) &&
                attempt < maxAcquireAttempts)
            {
                for (var index = acquired.Count - 1; index >= 0; index--)
                    await acquired[index].DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
            catch
            {
                for (var index = acquired.Count - 1; index >= 0; index--)
                    await acquired[index].DisposeAsync();
                throw;
            }
        }

        throw new InvalidOperationException(
            "DYNAMIC_FLOW_RUNTIME_RECONCILE_LEASE_REFRESH_RETRY_EXHAUSTED");
    }

    private async Task<string> AcquireRuntimeInstanceReconcileLeaseAsync(
        string flowInstanceId,
        CancellationToken ct)
    {
        const int maxAcquireAttempts = 300;
        for (var attempt = 1; attempt <= maxAcquireAttempts; attempt++)
        {
            var now = DateTime.UtcNow;
            var leaseId = ObjectId.GenerateNewId().ToString();
            var fb = Builders<DynamicFlowInstance>.Filter;
            var available =
                fb.Eq(x => x.RuntimeReconcileLeaseId, null) |
                fb.Exists(x => x.RuntimeReconcileLeaseId, false) |
                fb.Lte(x => x.RuntimeReconcileLeaseExpiresAtUtc, now);
            var acquired = await _ctx.DynamicFlowInstances.FindOneAndUpdateAsync(
                fb.Eq(x => x.Id, flowInstanceId) &
                fb.Eq(x => x.IsDeleted, false) &
                available,
                Builders<DynamicFlowInstance>.Update
                    .Set(x => x.RuntimeReconcileLeaseId, leaseId)
                    .Set(
                        x => x.RuntimeReconcileLeaseExpiresAtUtc,
                        now.Add(RuntimeReconcileLeaseDuration))
                    .Inc(x => x.RuntimeMaterializationFenceRevision, 1),
                new FindOneAndUpdateOptions<DynamicFlowInstance>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            if (acquired is not null)
                return leaseId;

            var exists = await _ctx.DynamicFlowInstances
                .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
                .AnyAsync(ct);
            if (!exists)
                throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
            if (attempt < maxAcquireAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new InvalidOperationException(
            "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_LEASE_RETRY_EXHAUSTED");
    }

    private async Task RenewRuntimeInstanceReconcileLeaseAsync(
        string flowInstanceId,
        string leaseId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var renewed = await _ctx.DynamicFlowInstances.UpdateOneAsync(
            x => x.Id == flowInstanceId &&
                 x.RuntimeReconcileLeaseId == leaseId &&
                 x.RuntimeReconcileLeaseExpiresAtUtc > now &&
                 !x.IsDeleted,
            Builders<DynamicFlowInstance>.Update.Set(
                x => x.RuntimeReconcileLeaseExpiresAtUtc,
                now.Add(RuntimeReconcileLeaseDuration)),
            cancellationToken: ct);
        if (renewed.MatchedCount != 1)
        {
            throw new DynamicFlowRuntimeReconcileFenceException(
                "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_LEASE_LOST");
        }
    }

    private async Task ReleaseRuntimeInstanceReconcileLeaseAsync(
        string flowInstanceId,
        string leaseId)
    {
        await _ctx.DynamicFlowInstances.UpdateOneAsync(
            x => x.Id == flowInstanceId &&
                 x.RuntimeReconcileLeaseId == leaseId,
            Builders<DynamicFlowInstance>.Update
                .Unset(x => x.RuntimeReconcileLeaseId)
                .Unset(x => x.RuntimeReconcileLeaseExpiresAtUtc)
                .Inc(x => x.RuntimeMaterializationFenceRevision, 1),
            cancellationToken: CancellationToken.None);
    }

    private async Task RunRuntimeReconcileHeartbeatAsync(
        string flowInstanceId,
        string runtimeReconcileLeaseId,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        CancellationTokenSource heartbeatCancellation,
        CancellationTokenSource operationCancellation,
        TaskCompletionSource<Exception> failure)
    {
        try
        {
            while (true)
            {
                await Task.Delay(
                    RuntimeReconcileHeartbeatInterval,
                    heartbeatCancellation.Token);
                await RenewRuntimeInstanceReconcileLeaseAsync(
                    flowInstanceId,
                    runtimeReconcileLeaseId,
                    heartbeatCancellation.Token);
                await RenewRuntimeReconcileLeasesAsync(
                    reconcileLeases,
                    heartbeatCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (
            heartbeatCancellation.IsCancellationRequested)
        {
            // Normal scope shutdown or caller cancellation.
        }
        catch (Exception error)
        {
            failure.TrySetResult(error);
            operationCancellation.Cancel();
        }
    }

    private async Task<bool> HasLiveMaterializationClaimAsync(
        string flowInstanceId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await _ctx.DynamicFlowRuntimeOutbox
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.Status == DynamicFlowRuntimeOutboxStatuses.Processing &&
                x.LeaseUntilUtc != null &&
                x.LeaseUntilUtc > now)
            .AnyAsync(ct);
    }

    private async Task RepairUnprojectedReportLifecycleAsync(
        string flowInstanceId,
        string reportId,
        CancellationToken ct)
    {
        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_REPORT_PROJECTION_SOURCE_MISSING");
        var projectedSourceKeys = (await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    x.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                    x.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle &&
                    x.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
                .Project(x => x.CommandId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var missingEntries = (report.LifecycleProjectionOutbox ??
                              new List<WorkReportLifecycleProjectionOutboxEntry>())
            .Where(entry =>
                !projectedSourceKeys.Contains(
                    Hash($"{report.Id}\n{entry.EntryKey}")))
            .OrderBy(entry => entry.LifecycleRevision)
            .ThenBy(entry => entry.EntryKey, StringComparer.Ordinal)
            .ToArray();
        if (missingEntries.Length > 0)
        {
            await _stateProjector.ProjectReportLifecycleAsync(
                report,
                missingEntries,
                ct);
        }
    }

    private async Task<DynamicFlowRuntimeStateReconcileResult> DrainReportLifecycleDebtAsync(
        string flowInstanceId,
        bool apply,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        CancellationToken ct)
    {
        var assignmentIds = await _ctx.WorkAssignments
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);
        if (assignmentIds.Count == 0)
        {
            return new DynamicFlowRuntimeStateReconcileResult(
                flowInstanceId,
                apply,
                AssignmentCount: 0,
                DriftCount: 0,
                ProjectedCount: 0,
                DriftAssignmentIds: Array.Empty<string>(),
                PendingReportIds: Array.Empty<string>(),
                UnprojectedReportIds: Array.Empty<string>());
        }

        var reports = await _ctx.WorkAssignmentReports
            .Find(x =>
                assignmentIds.Contains(x.WorkAssignmentId) &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var pendingReportIds = reports
            .Where(report =>
                (report.LifecycleProjectionOutbox ??
                 new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Any(entry =>
                    entry.State !=
                    WorkReportLifecycleProjectionOutboxStates.Completed))
            .Select(report => report.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        foreach (var reportId in pendingReportIds)
        {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await _lifecycleReconciler.ReconcileReportAsync(reportId, ct);
        }

        reports = await _ctx.WorkAssignmentReports
            .Find(x =>
                assignmentIds.Contains(x.WorkAssignmentId) &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var projectedSourceKeys = (await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    x.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                    x.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle &&
                    x.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
                .Project(x => x.CommandId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var unprojectedReportIds = reports
            .Where(report =>
                (report.LifecycleProjectionOutbox ??
                 new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Any(entry =>
                    !projectedSourceKeys.Contains(
                        Hash($"{report.Id}\n{entry.EntryKey}"))))
            .Select(report => report.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        foreach (var reportId in unprojectedReportIds)
        {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await RepairUnprojectedReportLifecycleAsync(
                flowInstanceId,
                reportId,
                ct);
        }

        reports = await _ctx.WorkAssignmentReports
            .Find(x =>
                assignmentIds.Contains(x.WorkAssignmentId) &&
                !x.IsDeleted)
            .ToListAsync(ct);
        projectedSourceKeys = (await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    x.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                    x.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle &&
                    x.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
                .Project(x => x.CommandId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var remainingPendingReportIds = reports
            .Where(report =>
                (report.LifecycleProjectionOutbox ??
                 new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Any(entry =>
                    entry.State !=
                    WorkReportLifecycleProjectionOutboxStates.Completed))
            .Select(report => report.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var remainingUnprojectedReportIds = reports
            .Where(report =>
                (report.LifecycleProjectionOutbox ??
                 new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Any(entry =>
                    !projectedSourceKeys.Contains(
                        Hash($"{report.Id}\n{entry.EntryKey}"))))
            .Select(report => report.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var debtReportIds = remainingPendingReportIds
            .Concat(remainingUnprojectedReportIds)
            .ToHashSet(StringComparer.Ordinal);
        var driftAssignmentIds = reports
            .Where(report => debtReportIds.Contains(report.Id))
            .Select(report => report.WorkAssignmentId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new DynamicFlowRuntimeStateReconcileResult(
            flowInstanceId,
            apply,
            assignmentIds.Distinct(StringComparer.Ordinal).Count(),
            driftAssignmentIds.Length,
            ProjectedCount: 0,
            driftAssignmentIds,
            remainingPendingReportIds,
            remainingUnprojectedReportIds);
    }

    private async Task RepairOwnedMaterializationArtifactsAsync(
        DynamicFlowInstance instance,
        string actorUserId,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        string runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var snapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_MISSING");
        var steps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == instance.Id &&
                !x.IsDeleted)
            .ToListAsync(ct);
        if (!await HasExactRuntimeImmutableAuthorityAsync(
                instance,
                snapshot,
                steps,
                ct))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_IMMUTABLE_AUTHORITY_DRIFT");
        }
        if (!TryBuildOwnershipProof(
                instance,
                snapshot,
                steps,
                out var ownershipProof))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OWNERSHIP_PROOF_FAILED");
        }

        var formsById = new Dictionary<string, DynamicFormTemplate>(
            StringComparer.Ordinal);
        foreach (var branch in ownershipProof)
        {
            if (formsById.ContainsKey(branch.Step.FormVersionId))
                continue;
            var form = await _ctx.DynamicFormTemplates
                .Find(x =>
                    x.Id == branch.Step.FormVersionId &&
                    x.IsPublished &&
                    !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_FORM_PIN_UNAVAILABLE");
            var published =
                DynamicFormPublishedSchemaSnapshotBuilder
                    .ValidateAgainstTemplate(form);
            var familyId = string.IsNullOrWhiteSpace(form.FamilyId)
                ? form.Id
                : form.FamilyId;
            if (!string.Equals(
                    familyId,
                    branch.Step.FormFamilyId,
                    StringComparison.Ordinal) ||
                Math.Max(1, form.VersionNo) != branch.Step.FormVersionNo ||
                !FixedEquals(
                    published.Sha256,
                    branch.Step.FormSchemaHash) ||
                !FixedEquals(
                    published.Sha256,
                    branch.Step.FormSnapshotHash))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_FORM_PIN_STALE");
            }
            formsById[form.Id] = form;
        }

        var repairResult = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var instanceFence =
                    await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            instance.Id,
                            runtimeReconcileLeaseId,
                            now),
                        Builders<DynamicFlowInstance>.Update.Inc(
                            x => x.RuntimeMaterializationFenceRevision,
                            1),
                        cancellationToken: transactionCt);
                if (instanceFence.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_LEASE_LOST");
                }

                var expectedAssignmentIds = ownershipProof
                    .Select(branch => branch.AssignmentId)
                    .ToHashSet(StringComparer.Ordinal);
                var repairedPeriodIds = new List<string>();
                var stalePeriodIds = new List<string>();
                foreach (var branch in ownershipProof)
                {
                    var existingAssignment = await _ctx.WorkAssignments
                        .Find(session, x => x.Id == branch.AssignmentId)
                        .FirstOrDefaultAsync(transactionCt);
                    if (existingAssignment is not null &&
                        !existingAssignment.IsDeleted)
                    {
                        var lease = reconcileLeases.SingleOrDefault(item =>
                            item.AssignmentId == branch.AssignmentId);
                        if (lease is null)
                        {
                            throw new DynamicFlowRuntimeReconcileFenceException(
                                "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_LEASE_MISSING");
                        }
                        var leaseHeld =
                            await _ctx.WorkAssignments.CountDocumentsAsync(
                                session,
                                x =>
                                    x.Id == branch.AssignmentId &&
                                    x.ReportLifecycleLeaseId == lease.LeaseId &&
                                    x.ReportLifecycleLeaseExpiresAtUtc > now &&
                                    !x.IsDeleted,
                                cancellationToken: transactionCt);
                        if (leaseHeld != 1)
                        {
                            throw new DynamicFlowRuntimeReconcileFenceException(
                                "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_LEASE_LOST");
                        }
                    }

                    var form = formsById[branch.Step.FormVersionId];
                    var frozenUsers = FrozenUsers(branch.Binding.Participants);
                    var assignment = await UpsertOwnedAssignmentAsync(
                        session,
                        instance,
                        branch.Step,
                        form,
                        frozenUsers,
                        branch.AssignmentId,
                        transactionCt,
                        allowTerminalRepair: true);
                    var bindings = await UpsertBindingsAsync(
                        session,
                        assignment,
                        form,
                        frozenUsers,
                        transactionCt);
                    var expectedPeriodIds = new List<string>();
                    var expectedQueueIds = new List<string>();
                    foreach (var binding in bindings)
                    {
                        var period = await UpsertLaunchPeriodAsync(
                            session,
                            instance,
                            assignment,
                            binding,
                            transactionCt);
                        expectedPeriodIds.Add(period.Id);
                        repairedPeriodIds.Add(period.Id);
                        var queue = await UpsertRuntimeQueueAsync(
                            session,
                            period,
                            assignment.CompletedAtUtc.HasValue,
                            actorUserId,
                            transactionCt);
                        expectedQueueIds.Add(queue.Id);
                    }

                    var staleForAssignment = await _ctx.WorkReportPeriods
                        .Find(
                            session,
                            period =>
                                period.WorkAssignmentId == assignment.Id &&
                                period.IsActive &&
                                !period.IsDeleted &&
                                !expectedPeriodIds.Contains(period.Id))
                        .Project(period => period.Id)
                        .ToListAsync(transactionCt);
                    if (staleForAssignment.Count > 0)
                    {
                        await _ctx.WorkReportPeriods.UpdateManyAsync(
                            session,
                            period => staleForAssignment.Contains(period.Id),
                            Builders<WorkReportPeriod>.Update
                                .Set(period => period.IsActive, false)
                                .Set(period => period.UpdatedAtUtc, now)
                                .Set(period => period.UpdatedByUserId, actorUserId),
                            cancellationToken: transactionCt);
                        stalePeriodIds.AddRange(staleForAssignment);
                    }
                    await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
                        session,
                        queue =>
                            queue.WorkAssignmentId == assignment.Id &&
                            !queue.IsDeleted &&
                            !expectedQueueIds.Contains(queue.Id),
                        Builders<WorkAssignmentQueueItem>.Update
                            .Set(queue => queue.IsActive, false)
                            .Set(queue => queue.IsDeleted, true)
                            .Set(queue => queue.DeletedAtUtc, now)
                            .Set(queue => queue.DeletedByUserId, actorUserId)
                            .Set(queue => queue.UpdatedAtUtc, now)
                            .Set(queue => queue.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                }

                var rogueAssignments = await _ctx.WorkAssignments
                    .Find(
                        session,
                        assignment =>
                            assignment.FlowInstanceId == instance.Id &&
                            assignment.IsActive &&
                            !assignment.IsDeleted &&
                            !expectedAssignmentIds.Contains(assignment.Id))
                    .ToListAsync(transactionCt);
                foreach (var rogue in rogueAssignments)
                {
                    var lease = reconcileLeases.SingleOrDefault(item =>
                        item.AssignmentId == rogue.Id);
                    if (lease is null)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_LEASE_MISSING");
                    }
                    var deactivated =
                        await _ctx.WorkAssignments.UpdateOneAsync(
                            session,
                            x =>
                                x.Id == rogue.Id &&
                                x.ReportLifecycleLeaseId == lease.LeaseId &&
                                x.ReportLifecycleLeaseExpiresAtUtc > now &&
                                x.IsActive &&
                                !x.IsDeleted,
                            Builders<WorkAssignment>.Update
                                .Set(x => x.IsActive, false)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId)
                                .Inc(
                                    x =>
                                        x.DynamicFlowMaterializationRevision,
                                    1),
                            cancellationToken: transactionCt);
                    if (deactivated.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_LEASE_LOST");
                    }
                    await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                        session,
                        x =>
                            x.WorkAssignmentId == rogue.Id &&
                            x.IsActive &&
                            !x.IsDeleted,
                        Builders<WorkTemplateAssignee>.Update
                            .Set(x => x.IsActive, false)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                    var roguePeriodIds = await _ctx.WorkReportPeriods
                        .Find(
                            session,
                            x =>
                                x.WorkAssignmentId == rogue.Id &&
                                x.IsActive &&
                                !x.IsDeleted)
                        .Project(x => x.Id)
                        .ToListAsync(transactionCt);
                    if (roguePeriodIds.Count > 0)
                    {
                        await _ctx.WorkReportPeriods.UpdateManyAsync(
                            session,
                            x => roguePeriodIds.Contains(x.Id),
                            Builders<WorkReportPeriod>.Update
                                .Set(x => x.IsActive, false)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId),
                            cancellationToken: transactionCt);
                        stalePeriodIds.AddRange(roguePeriodIds);
                    }
                    await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
                        session,
                        x =>
                            x.WorkAssignmentId == rogue.Id &&
                            !x.IsDeleted,
                        Builders<WorkAssignmentQueueItem>.Update
                            .Set(x => x.IsActive, false)
                            .Set(x => x.IsDeleted, true)
                            .Set(x => x.DeletedAtUtc, now)
                            .Set(x => x.DeletedByUserId, actorUserId)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                }

                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    instance.WorkId,
                    transactionCt);
                return new OwnedArtifactRepairResult(
                    repairedPeriodIds
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    stalePeriodIds
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    rogueAssignments
                        .Select(assignment => assignment.Id)
                        .Distinct(StringComparer.Ordinal)
                        .ToList());
            },
            ct);

        foreach (var rogueAssignmentId in repairResult.RogueAssignmentIds)
        {
            await _docRoles.DeleteDocRolesAsync(
                DocType.WORK_ASSIGNMENT,
                rogueAssignmentId,
                actorUserId,
                ct);
            await _projection.RebuildAssignmentAsync(
                rogueAssignmentId,
                actorUserId,
                ct);
        }
        foreach (var stalePeriodId in repairResult.StalePeriodIds)
        {
            await _projection.RebuildReportPeriodAsync(
                stalePeriodId,
                actorUserId,
                ct);
        }
    }

    private async Task RepairProjectionReadModelsAsync(
        DynamicFlowInstance instance,
        string actorUserId,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        string runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var assignments = await _ctx.WorkAssignments
            .Find(x =>
                x.FlowInstanceId == instance.Id &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);
        foreach (var assignment in assignments)
        {
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await RepairAssignmentCompletionProjectionAsync(
                assignment,
                actorUserId,
                reconcileLeases.Single(lease =>
                    lease.AssignmentId == assignment.Id),
                runtimeReconcileLeaseId,
                ct);
            var periods = await _ctx.WorkReportPeriods
                .Find(x =>
                    x.WorkAssignmentId == assignment.Id &&
                    x.IsActive &&
                    !x.IsDeleted)
                .ToListAsync(ct);
            var projectedPeriodIds = new List<string>();
            foreach (var period in periods)
            {
                await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
                var lifecycleRepaired =
                    await _lifecycleReconciler.RepairCurrentPeriodAsync(
                    period.Id,
                    actorUserId,
                    ct);
                if (!lifecycleRepaired)
                {
                    await RepairInitialRuntimePeriodAsync(
                        period,
                        actorUserId,
                        ct);
                }
                var projectedPeriod = await _ctx.WorkReportPeriods
                    .Find(x => x.Id == period.Id && x.IsActive && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);
                if (projectedPeriod is null)
                    continue;
                await _queue.UpsertPeriodAsync(projectedPeriod, actorUserId, ct);
                projectedPeriodIds.Add(projectedPeriod.Id);
            }
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await _progress.RecomputeSingleAsync(assignment.Id, ct);
            var repairedAssignment = await _ctx.WorkAssignments
                .Find(x => x.Id == assignment.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (repairedAssignment is null)
                continue;
            foreach (var projectedPeriodId in projectedPeriodIds)
            {
                await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
                await _projection.RebuildReportPeriodAsync(
                    projectedPeriodId,
                    actorUserId,
                    ct);
            }
            await _docRoles.UpsertWorkAssignmentRolesAsync(repairedAssignment, ct);
            await _projection.RebuildAssignmentAsync(
                repairedAssignment.Id,
                actorUserId,
                ct);
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            var canonicalAssignment = await _ctx.WorkAssignments
                .Find(x => x.Id == repairedAssignment.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (canonicalAssignment is null ||
                !canonicalAssignment.IsActive ||
                canonicalAssignment.CompletedAtUtc.HasValue)
            {
                await _queue.DisableByAssignmentAsync(
                    assignment.Id,
                    actorUserId,
                    ct);
            }
        }
        await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
        await _docRoles.RebuildWorkParticipantRolesFromAssignmentsAsync(
            instance.WorkId,
            actorUserId,
            ct);
    }

    private async Task<bool> RepairInitialRuntimePeriodAsync(
        WorkReportPeriod observed,
        string actorUserId,
        CancellationToken ct)
    {
        var hasReport = await _ctx.WorkAssignmentReports.CountDocumentsAsync(
            x =>
                x.WorkReportPeriodId == observed.Id &&
                !x.IsDeleted,
            cancellationToken: ct);
        if (hasReport != 0)
            return false;

        var now = DateTime.UtcNow;
        var filter = Builders<WorkReportPeriod>.Filter;
        var result = await _ctx.WorkReportPeriods.UpdateOneAsync(
            filter.Eq(x => x.Id, observed.Id) &
            filter.Eq(x => x.IsDeleted, false) &
            filter.Eq(x => x.UpdatedAtUtc, observed.UpdatedAtUtc) &
            filter.Eq(x => x.CurrentReportId, observed.CurrentReportId) &
            filter.Eq(
                x => x.SourceLifecycleReportId,
                observed.SourceLifecycleReportId) &
            filter.Eq(
                x => x.SourceLifecycleRevision,
                observed.SourceLifecycleRevision),
            Builders<WorkReportPeriod>.Update
                .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
                .Set(x => x.ReportDate, null)
                .Set(x => x.StartedDate, null)
                .Set(x => x.CompletedDate, null)
                .Set(x => x.PeriodStart, null)
                .Set(x => x.PeriodEnd, null)
                .Set(x => x.DueAtUtc, null)
                .Set(x => x.Status, WorkReportPeriodStatus.Pending)
                .Set(x => x.IsOverdue, false)
                .Set(x => x.IsHistoricalData, false)
                .Set(x => x.HistoricalDataApproved, false)
                .Set(x => x.HistoricalDataApprovedAtUtc, null)
                .Set(x => x.HistoricalDataApprovedByUserId, null)
                .Set(x => x.CurrentReportId, null)
                .Set(x => x.SourceLifecycleReportId, null)
                .Set(x => x.SourceLifecycleRevision, 0)
                .Set(x => x.SourceLifecycleAppliedAtUtc, null)
                .Set(x => x.ReportVersionCount, 0)
                .Set(x => x.LastDraftSavedAtUtc, null)
                .Set(x => x.LastSubmittedAtUtc, null)
                .Set(x => x.LastReviewedAtUtc, null)
                .Set(x => x.RequiresLateReason, false)
                .Set(x => x.AcceptedLateReason, null)
                .Set(x => x.LateReason, null)
                .Set(x => x.ReviewerComment, null)
                .Set(x => x.ReviewerEvaluation, null)
                .Set(x => x.ReturnReason, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
        if (result.MatchedCount == 1)
        {
            await _ctx.WorkAssignments.UpdateOneAsync(
                x => x.Id == observed.WorkAssignmentId && !x.IsDeleted,
                Builders<WorkAssignment>.Update.Inc(
                    x => x.ReportLifecycleSeriesRevision,
                    1),
                cancellationToken: ct);
        }
        return result.MatchedCount == 1;
    }

    private async Task RepairAssignmentCompletionProjectionAsync(
        WorkAssignment assignment,
        string actorUserId,
        WorkReportLifecycleSeriesLease reconcileLease,
        string runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var commandId = $"assignment-completed:{assignment.Id}";
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x =>
                x.FlowInstanceId == assignment.FlowInstanceId &&
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
                x.CommandId == commandId &&
                x.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
            .FirstOrDefaultAsync(ct);
        if (receipt is null && assignment.CompletedAtUtc.HasValue)
        {
            await _stateProjector.ProjectAssignmentCompletionAsync(
                assignment.Id,
                assignment.CompletedByUserId ?? actorUserId,
                ct);
            receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x =>
                    x.FlowInstanceId == assignment.FlowInstanceId &&
                    x.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
                    x.CommandId == commandId &&
                    x.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
                .FirstOrDefaultAsync(ct);
        }

        if (receipt is not null &&
            TryReadAssignmentCompletionProjection(receipt, out var completion) &&
            completion.AssignmentId == assignment.Id &&
            await IsTrustedAssignmentCompletionReceiptAsync(
                receipt,
                assignment,
                completion,
                ct))
        {
            await ApplyAssignmentCompletionRepairAsync(
                assignment,
                reconcileLease,
                runtimeReconcileLeaseId,
                Builders<WorkAssignment>.Update
                    .Set(x => x.CompletedAtUtc, completion.CompletedAtUtc)
                    .Set(x => x.CompletedDate, completion.CompletedDate)
                    .Set(x => x.CompletedByUserId, completion.CompletedByUserId)
                    .Set(
                        x => x.ProgressStatus,
                        (int)WorkAssignmentProgressStatus.Completed)
                    .Set(
                        x => x.ProgressStatusUpdatedAtUtc,
                        completion.CompletedAtUtc),
                ct);
            return;
        }

        if (!assignment.CompletedAtUtc.HasValue)
        {
            await ApplyAssignmentCompletionRepairAsync(
                assignment,
                reconcileLease,
                runtimeReconcileLeaseId,
                Builders<WorkAssignment>.Update
                    .Set(x => x.CompletedDate, null)
                    .Set(x => x.CompletedByUserId, null),
                ct);
        }
    }

    private Task ApplyAssignmentCompletionRepairAsync(
        WorkAssignment assignment,
        WorkReportLifecycleSeriesLease reconcileLease,
        string runtimeReconcileLeaseId,
        UpdateDefinition<WorkAssignment> update,
        CancellationToken ct)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var instanceFence =
                    await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            assignment.FlowInstanceId!,
                            runtimeReconcileLeaseId,
                            now),
                        Builders<DynamicFlowInstance>.Update.Inc(
                            x => x.RuntimeMaterializationFenceRevision,
                            1),
                        cancellationToken: transactionCt);
                if (instanceFence.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_COMPLETION_REPAIR_FENCE_CONFLICT");
                }
                var repaired = await _ctx.WorkAssignments.UpdateOneAsync(
                    session,
                    x =>
                        x.Id == assignment.Id &&
                        x.FlowInstanceId == assignment.FlowInstanceId &&
                        x.DynamicFlowMaterializationRevision ==
                        assignment.DynamicFlowMaterializationRevision &&
                        x.ReportLifecycleLeaseId ==
                        reconcileLease.LeaseId &&
                        x.ReportLifecycleLeaseExpiresAtUtc > now &&
                        !x.IsDeleted,
                    update.Inc(
                        x => x.DynamicFlowMaterializationRevision,
                        1),
                    cancellationToken: transactionCt);
                if (repaired.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_COMPLETION_REPAIR_FENCE_CONFLICT");
                }
            },
            ct);

    private async Task<bool> IsTrustedAssignmentCompletionReceiptAsync(
        DynamicFlowRuntimeCommandReceipt receipt,
        WorkAssignment assignment,
        RuntimeAssignmentCompletionProjection completion,
        CancellationToken ct)
    {
        if (receipt.ResultSnapshot is null ||
            string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
        {
            return false;
        }
        var instance = await _ctx.DynamicFlowInstances
            .Find(x =>
                x.Id == assignment.FlowInstanceId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (instance is null)
            return false;

        var commandId = $"assignment-completed:{assignment.Id}";
        var actorUserId = receipt.UpdatedByUserId;
        var requestHash = string.IsNullOrWhiteSpace(actorUserId)
            ? null
            : Hash(string.Join(
                "\n",
                assignment.Id,
                completion.CompletedAtUtc
                    .ToUniversalTime()
                    .ToString("O"),
                completion.CompletedDate?
                    .ToUniversalTime()
                    .ToString("O") ?? string.Empty,
                actorUserId));
        var projectionFingerprint = requestHash is null
            ? null
            : Hash(string.Join(
                "\n",
                "assignment-completion",
                assignment.Id,
                completion.CompletedAtUtc
                    .ToUniversalTime()
                    .ToString("O"),
                requestHash));
        var snapshot = receipt.ResultSnapshot;
        if (!TryReadString(
                snapshot,
                "stepInstanceId",
                out var snapshotStepId))
        {
            return false;
        }
        var stepMatches =
            await _ctx.DynamicFlowStepInstances.CountDocumentsAsync(
                x =>
                    x.Id == snapshotStepId &&
                    x.FlowInstanceId == instance.Id &&
                    x.AssignmentId == assignment.Id &&
                    !x.IsDeleted,
                cancellationToken: ct);
        return requestHash is not null &&
               projectionFingerprint is not null &&
               receipt.Id == StableObjectId(
                   $"{DynamicFlowCommandScopeKinds.Instance}\n{instance.Id}\n{DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete}\n{commandId}") &&
               receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
               receipt.ScopeId == instance.Id &&
               receipt.WorkId == instance.WorkId &&
               receipt.FlowTemplateVersionId ==
               instance.FlowTemplateVersionId &&
               receipt.FlowInstanceId == instance.Id &&
               receipt.CommandType ==
               DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
               receipt.CommandId == commandId &&
               receipt.Status ==
               DynamicFlowRuntimeCommandStatuses.Succeeded &&
               completion.CompletedByUserId == actorUserId &&
               FixedEquals(receipt.RequestHash, requestHash) &&
               FixedEquals(
                   receipt.CommandIdentityHash,
                   Hash(
                       $"{actorUserId}\n{assignment.Id}\n{commandId}\n{commandId}")) &&
               FixedEquals(
                   receipt.SnapshotToken,
                   Hash(
                       $"{requestHash}\n{projectionFingerprint}\n{instance.FlowPayloadHash}")) &&
               FixedEquals(
                   receipt.ResultSnapshotHash,
                   Hash(snapshot.ToJson())) &&
               TryReadString(
                   snapshot,
                   "flowInstanceId",
                   out var snapshotInstanceId) &&
               snapshotInstanceId == instance.Id &&
               stepMatches == 1 &&
               TryReadString(
                   snapshot,
                   "commandType",
                   out var snapshotCommandType) &&
               snapshotCommandType ==
               DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
               TryReadString(
                   snapshot,
                   "sourceCommandId",
                   out var sourceCommandId) &&
               sourceCommandId == commandId &&
               TryReadString(
                   snapshot,
                   "sourceEventKey",
                   out var sourceEventKey) &&
               sourceEventKey == commandId &&
               TryReadString(
                   snapshot,
                   "projectionFingerprint",
                   out var snapshotFingerprint) &&
               FixedEquals(
                   snapshotFingerprint,
                   projectionFingerprint);
    }

    private static DynamicFlowRuntimeReconcileResult MergeStateReconcile(
        DynamicFlowRuntimeReconcileResult ledger,
        DynamicFlowRuntimeStateReconcileResult? state,
        int appliedCount = 0)
        => new()
        {
            FlowInstanceId = ledger.FlowInstanceId,
            Apply = ledger.Apply,
            StateBefore = ledger.StateBefore,
            StateAfter = ledger.StateAfter,
            ExpectedBranches = ledger.ExpectedBranches,
            AssignmentCount = ledger.AssignmentCount,
            BindingCount = ledger.BindingCount,
            PeriodCount = ledger.PeriodCount,
            QueueCount = ledger.QueueCount,
            AssignmentDocRoleCount = ledger.AssignmentDocRoleCount,
            WorkParticipantDocRoleCount = ledger.WorkParticipantDocRoleCount,
            AssignmentReadModelCount = ledger.AssignmentReadModelCount,
            PeriodReadModelCount = ledger.PeriodReadModelCount,
            CompletedOutboxCount = ledger.CompletedOutboxCount,
            PendingOutboxCount = ledger.PendingOutboxCount,
            ExactPinsAndRefs = ledger.ExactPinsAndRefs,
            ExactMaterializationLedgerConverged =
                ledger.ExactMaterializationLedgerConverged,
            ExactStateProjectionLedgerConverged =
                ledger.ExactStateProjectionLedgerConverged &&
                (state?.DriftCount ?? 0) == 0,
            StateProjectionDriftCount = state?.DriftCount ?? 0,
            StateProjectionAppliedCount = appliedCount,
            StateProjectionDriftAssignmentIds =
                state?.DriftAssignmentIds ?? Array.Empty<string>(),
            ExactLedgerConverged =
                ledger.ExactLedgerConverged &&
                (state?.DriftCount ?? 0) == 0,
            Converged =
                ledger.Converged &&
                (state?.DriftCount ?? 0) == 0
        };

    private async Task<bool> RepairRuntimeEventCursorAsync(
        string flowInstanceId,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        string runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (instance is null)
            return false;
        var observedCursor = instance.NextEventSequence;
        var events = await _ctx.DynamicFlowRuntimeEvents
            .Find(x => x.FlowInstanceId == flowInstanceId)
            .ToListAsync(ct);
        var expectedCursor = events.Count + 1L;
        if (observedCursor == expectedCursor ||
            events.Count == 0 ||
            !events.Select(item => item.Sequence)
                .OrderBy(value => value)
                .SequenceEqual(
                    Enumerable.Range(1, events.Count)
                        .Select(value => (long)value)))
        {
            return false;
        }

        var steps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var snapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct);
        if (snapshot is null ||
            !await HasExactRuntimeImmutableAuthorityAsync(
                instance,
                snapshot,
                steps,
                ct) ||
            !TryBuildOwnershipProof(
                instance,
                snapshot,
                steps,
                out var proof))
        {
            return false;
        }
        var receipts = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.FlowInstanceId == flowInstanceId)
            .ToListAsync(ct);
        var launchReceipts = receipts
            .Where(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                receipt.CommandType == "LAUNCH")
            .ToArray();
        var stateReceipts = receipts
            .Where(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                receipt.CommandType is
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle or
                    DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete or
                    DynamicFlowRuntimeStateProjectionOperations.StateReconcile or
                    DynamicFlowRuntimeStateProjectionOperations.SubflowChildTerminal)
            .ToArray();
        var forwardReceipts = receipts
            .Where(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                receipt.CommandType == "FORWARD")
            .ToArray();
        if (launchReceipts.Length != 1 ||
            launchReceipts[0].Status !=
            DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipts.Count !=
            launchReceipts.Length +
            stateReceipts.Length +
            forwardReceipts.Length)
        {
            return false;
        }
        var outboxItems = await _ctx.DynamicFlowRuntimeOutbox
            .Find(x => x.FlowInstanceId == flowInstanceId)
            .ToListAsync(ct);
        var assignmentIds = proof
            .Select(branch => branch.AssignmentId)
            .ToArray();
        var reports = await _ctx.WorkAssignmentReports
            .Find(x =>
                assignmentIds.Contains(x.WorkAssignmentId) &&
                !x.IsDeleted)
            .ToListAsync(ct);

        instance.NextEventSequence = expectedCursor;
        if (!ValidateRuntimeReceipt(
                instance,
                snapshot,
                proof,
                launchReceipts[0],
                outboxItems) ||
            !ValidateRuntimeOutbox(
                instance,
                snapshot,
                proof,
                outboxItems,
                forwardReceipts) ||
            !ValidateRuntimeEvents(
                instance,
                proof,
                launchReceipts[0],
                forwardReceipts,
                stateReceipts,
                outboxItems,
                events,
                reports))
        {
            return false;
        }

        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                foreach (var lease in reconcileLeases)
                {
                    var held = await _ctx.WorkAssignments
                        .CountDocumentsAsync(
                            session,
                            x =>
                                x.Id == lease.AssignmentId &&
                                x.FlowInstanceId == flowInstanceId &&
                                x.ReportLifecycleLeaseId == lease.LeaseId &&
                                x.ReportLifecycleLeaseExpiresAtUtc > now &&
                                !x.IsDeleted,
                            cancellationToken: transactionCt);
                    if (held != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_LEASE_LOST");
                    }
                }
                var repaired = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    RuntimeMaterializationFenceFilter(
                        flowInstanceId,
                        runtimeReconcileLeaseId,
                        now) &
                    DynamicFlowRuntimeRevisionContract.InstanceCas(
                        flowInstanceId,
                        instance.Revision,
                        instance.State) &
                    Builders<DynamicFlowInstance>.Filter.Eq(
                        x => x.NextEventSequence,
                        observedCursor),
                    Builders<DynamicFlowInstance>.Update
                        .Set(
                            x => x.NextEventSequence,
                            expectedCursor)
                        .Inc(
                            x =>
                                x.RuntimeMaterializationFenceRevision,
                            1),
                    cancellationToken: transactionCt);
                if (repaired.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_EVENT_CURSOR_REPAIR_CONFLICT");
                }
            },
            ct);
        return true;
    }

    private async Task PrepareLedgerRepairAsync(
        string flowInstanceId,
        string actorUserId,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        string runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var commandId = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId)
            .Project(x => x.LaunchCommandId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeLedgerRepairWrite);
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var repairInstance = await _ctx.DynamicFlowInstances
                    .Find(session, x => x.Id == flowInstanceId && !x.IsDeleted)
                    .SingleAsync(transactionCt);
                if (repairInstance.State is DynamicFlowInstanceStates.Failed
                    or DynamicFlowInstanceStates.Completed
                    or DynamicFlowInstanceStates.Terminated)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_TERMINAL_INSTANCE");
                }
                if (!string.Equals(
                        repairInstance.RuntimeReconcileLeaseId,
                        runtimeReconcileLeaseId,
                        StringComparison.Ordinal) ||
                    repairInstance.RuntimeReconcileLeaseExpiresAtUtc <= now)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_LEASE_LOST");
                }
                if (repairInstance.State != DynamicFlowInstanceStates.Retrying)
                {
                    var epochAdvanced =
                        await _ctx.DynamicFlowInstances.UpdateOneAsync(
                            session,
                            RuntimeMaterializationFenceFilter(
                                flowInstanceId,
                                runtimeReconcileLeaseId,
                                now) &
                            DynamicFlowRuntimeRevisionContract.InstanceCas(
                                flowInstanceId,
                                repairInstance.Revision,
                                repairInstance.State) &
                            Builders<DynamicFlowInstance>.Filter.Eq(
                                x => x.RuntimeRecoveryEpoch,
                                repairInstance.RuntimeRecoveryEpoch),
                            Builders<DynamicFlowInstance>.Update
                                .Inc(x => x.RuntimeRecoveryEpoch, 1)
                                .Inc(
                                    x =>
                                        x.RuntimeMaterializationFenceRevision,
                                    1),
                            cancellationToken: transactionCt);
                    if (epochAdvanced.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECOVERY_EPOCH_CONFLICT");
                    }
                    repairInstance.RuntimeRecoveryEpoch++;
                }

                var assignmentCount =
                    await _ctx.WorkAssignments.CountDocumentsAsync(
                        session,
                        x =>
                            x.FlowInstanceId == flowInstanceId &&
                            !x.IsDeleted,
                        cancellationToken: transactionCt);
                if (assignmentCount != reconcileLeases.Count)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_SET_CHANGED");
                }
                foreach (var lease in reconcileLeases)
                {
                    var fenced = await _ctx.WorkAssignments.UpdateOneAsync(
                        session,
                        x =>
                            x.Id == lease.AssignmentId &&
                            x.FlowInstanceId == flowInstanceId &&
                            x.ReportLifecycleLeaseId == lease.LeaseId &&
                            !x.IsDeleted,
                        Builders<WorkAssignment>.Update.Inc(
                            x => x.DynamicFlowMaterializationRevision,
                            1),
                        cancellationToken: transactionCt);
                    if (fenced.MatchedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_ASSIGNMENT_LEASE_LOST");
                    }
                }

                await _ctx.DynamicFlowRuntimeOutbox.UpdateManyAsync(
                    session,
                    x =>
                        x.FlowInstanceId == flowInstanceId &&
                        (x.Status != DynamicFlowRuntimeOutboxStatuses.Processing ||
                         x.LeaseUntilUtc == null ||
                         x.LeaseUntilUtc <= now),
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Inc(x => x.RepairEpoch, 1)
                        .Set(x => x.Status, DynamicFlowRuntimeOutboxStatuses.Pending)
                        .Set(x => x.NextAttemptAtUtc, now)
                        .Set(x => x.CompletedAtUtc, null)
                        .Set(x => x.LeaseId, null)
                        .Set(x => x.LeaseUntilUtc, null)
                        .Set(x => x.LastErrorCode, null)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                if (repairInstance.State == DynamicFlowInstanceStates.Reconciled)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        repairInstance.State,
                        DynamicFlowInstanceStates.Active);
                    var restored = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            flowInstanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            flowInstanceId,
                            repairInstance.Revision,
                            repairInstance.State),
                        Builders<DynamicFlowInstance>.Update
                            .Set(x => x.State, DynamicFlowInstanceStates.Active)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1)
                            .Inc(x => x.RuntimeMaterializationFenceRevision, 1),
                        cancellationToken: transactionCt);
                    if (restored.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_STATE_CONFLICT");
                    }
                    repairInstance.State = DynamicFlowInstanceStates.Active;
                    repairInstance.Revision++;
                }
                if (repairInstance.State is DynamicFlowInstanceStates.Active
                    or DynamicFlowInstanceStates.Materializing)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        repairInstance.State,
                        DynamicFlowInstanceStates.Partial);
                    var partial = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            flowInstanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            flowInstanceId,
                            repairInstance.Revision,
                            repairInstance.State),
                        Builders<DynamicFlowInstance>.Update
                            .Set(x => x.State, DynamicFlowInstanceStates.Partial)
                            .Set(
                                x => x.ResumeState,
                                DynamicFlowInstanceStates.Active)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1)
                            .Inc(x => x.RuntimeMaterializationFenceRevision, 1),
                        cancellationToken: transactionCt);
                    if (partial.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_STATE_CONFLICT");
                    }
                    repairInstance.State = DynamicFlowInstanceStates.Partial;
                    repairInstance.Revision++;
                }
                if (repairInstance.State == DynamicFlowInstanceStates.Partial)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        repairInstance.State,
                        DynamicFlowInstanceStates.Retrying);
                    var retrying = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            flowInstanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            flowInstanceId,
                            repairInstance.Revision,
                            repairInstance.State),
                        Builders<DynamicFlowInstance>.Update
                            .Set(x => x.State, DynamicFlowInstanceStates.Retrying)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1)
                            .Inc(x => x.RuntimeMaterializationFenceRevision, 1),
                        cancellationToken: transactionCt);
                    if (retrying.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_RECONCILE_INSTANCE_STATE_CONFLICT");
                    }
                    repairInstance.State = DynamicFlowInstanceStates.Retrying;
                    repairInstance.Revision++;
                }
                var repairSteps = await _ctx.DynamicFlowStepInstances
                    .Find(
                        session,
                        x => x.FlowInstanceId == flowInstanceId && !x.IsDeleted)
                    .ToListAsync(transactionCt);
                foreach (var repairStep in repairSteps)
                {
                    if (repairStep.State == DynamicFlowStepStates.Completed)
                        continue;
                    var resumeState = repairStep.State == DynamicFlowStepStates.Materializing
                        ? DynamicFlowStepStates.Assigned
                        : ResolveStepResumeState(repairStep);
                    if (repairStep.State == DynamicFlowStepStates.Reconciled)
                    {
                        DynamicFlowRuntimeStateContract.RequireStepTransition(
                            repairStep.State,
                            resumeState);
                        var restored = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.StepCas(
                                repairStep.Id,
                                repairStep.Revision,
                                repairStep.State),
                            Builders<DynamicFlowStepInstance>.Update
                                .Set(x => x.State, resumeState)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId)
                                .Inc(x => x.Revision, 1),
                            cancellationToken: transactionCt);
                        if (restored.ModifiedCount != 1)
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
                        repairStep.State = resumeState;
                        repairStep.Revision++;
                    }
                    if (repairStep.State is DynamicFlowStepStates.Assigned
                        or DynamicFlowStepStates.InProgress
                        or DynamicFlowStepStates.Submitted
                        or DynamicFlowStepStates.Returned
                        or DynamicFlowStepStates.Approved
                        or DynamicFlowStepStates.Materializing)
                    {
                        DynamicFlowRuntimeStateContract.RequireStepTransition(
                            repairStep.State,
                            DynamicFlowStepStates.Partial);
                        var partial = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.StepCas(
                                repairStep.Id,
                                repairStep.Revision,
                                repairStep.State),
                            Builders<DynamicFlowStepInstance>.Update
                                .Set(x => x.State, DynamicFlowStepStates.Partial)
                                .Set(x => x.ResumeState, resumeState)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId)
                                .Inc(x => x.Revision, 1),
                            cancellationToken: transactionCt);
                        if (partial.ModifiedCount != 1)
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
                        repairStep.State = DynamicFlowStepStates.Partial;
                        repairStep.Revision++;
                    }
                    if (repairStep.State == DynamicFlowStepStates.Partial)
                    {
                        DynamicFlowRuntimeStateContract.RequireStepTransition(
                            repairStep.State,
                            DynamicFlowStepStates.Retrying);
                        var retrying = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.StepCas(
                                repairStep.Id,
                                repairStep.Revision,
                                repairStep.State),
                            Builders<DynamicFlowStepInstance>.Update
                                .Set(x => x.State, DynamicFlowStepStates.Retrying)
                                .Set(x => x.ResumeState, resumeState)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId)
                                .Inc(x => x.Revision, 1),
                            cancellationToken: transactionCt);
                        if (retrying.ModifiedCount != 1)
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
                    }
                }
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    repairInstance.WorkId,
                    transactionCt);
            },
            ct);
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterLedgerRepairWrite);
    }

    public async Task<int> CompensateOwnedArtifactsAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (!apply)
        {
            return await CompensateOwnedArtifactsCoreAsync(
                flowInstanceId,
                false,
                actorUserId,
                null,
                Array.Empty<WorkReportLifecycleSeriesLease>(),
                ct);
        }

        var runtimeReconcileLeaseId =
            await AcquireRuntimeInstanceReconcileLeaseAsync(
                flowInstanceId,
                ct);
        var reconcileLeases =
            new List<WorkReportLifecycleSeriesLease>();
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                operationCancellation.Token);
        var heartbeatFailure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? heartbeatTask = null;
        try
        {
            reconcileLeases =
                await AcquireRuntimeReconcileLeasesAsync(
                    flowInstanceId,
                    ct);
            heartbeatTask = RunRuntimeReconcileHeartbeatAsync(
                flowInstanceId,
                runtimeReconcileLeaseId,
                reconcileLeases,
                heartbeatCancellation,
                operationCancellation,
                heartbeatFailure);
            return await CompensateOwnedArtifactsCoreAsync(
                flowInstanceId,
                true,
                actorUserId,
                runtimeReconcileLeaseId,
                reconcileLeases,
                operationCancellation.Token);
        }
        catch (OperationCanceledException) when (
            heartbeatFailure.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_COMPENSATION_LEASE_HEARTBEAT_FAILED",
                await heartbeatFailure.Task);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            if (heartbeatTask is not null)
            {
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected while the compensation scope ends.
                }
            }
            for (var index = reconcileLeases.Count - 1;
                 index >= 0;
                 index--)
            {
                await reconcileLeases[index].DisposeAsync();
            }
            await ReleaseRuntimeInstanceReconcileLeaseAsync(
                flowInstanceId,
                runtimeReconcileLeaseId);
        }
    }

    private async Task<int> CompensateOwnedArtifactsCoreAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        string? runtimeReconcileLeaseId,
        IReadOnlyList<WorkReportLifecycleSeriesLease> reconcileLeases,
        CancellationToken ct)
    {
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        var participantSnapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_MISSING");
        var ownedSteps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                !x.IsDeleted)
            .ToListAsync(ct);
        if (!TryBuildOwnershipProof(
                instance,
                participantSnapshot,
                ownedSteps,
                out var ownershipProof))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OWNERSHIP_PROOF_FAILED");
        }
        var referencedAssignmentIds = ownershipProof
            .Select(branch => branch.AssignmentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var bindingClaimIds = ownershipProof
            .SelectMany(branch => branch.BindingIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignmentIds = referencedAssignmentIds.Count == 0
            ? await _ctx.WorkAssignments
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    !x.IsDeleted)
                .Project(x => x.Id)
                .ToListAsync(ct)
            : await _ctx.WorkAssignments
                .Find(x =>
                    (referencedAssignmentIds.Contains(x.Id) ||
                     x.FlowInstanceId == flowInstanceId) &&
                    !x.IsDeleted)
                .Project(x => x.Id)
                .ToListAsync(ct);
        assignmentIds = assignmentIds.Distinct(StringComparer.Ordinal).ToList();
        if (!apply)
            return Math.Max(assignmentIds.Count, bindingClaimIds.Count);
        if (instance.State != DynamicFlowInstanceStates.Failed)
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_COMPENSATION_REQUIRES_FAILED_INSTANCE");
        if (assignmentIds.Count == 0 && bindingClaimIds.Count == 0)
        {
            await CompleteCompensationAsync(
                flowInstanceId,
                actorUserId,
                runtimeReconcileLeaseId!,
                ct);
            return 0;
        }

        var periodIds = assignmentIds.Count == 0
            ? new List<string>()
            : await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkId == instance.WorkId &&
                assignmentIds.Contains(x.WorkAssignmentId))
            .Project(x => x.Id)
            .ToListAsync(ct);
        _faults.ThrowIfConfigured(
            instance.LaunchCommandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeCompensationWrite);
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var transactionNow = DateTime.UtcNow;
                var fenced =
                    await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeCompensationFenceFilter(
                            flowInstanceId,
                            runtimeReconcileLeaseId!,
                            transactionNow),
                        Builders<DynamicFlowInstance>.Update.Inc(
                            x => x.RuntimeMaterializationFenceRevision,
                            1),
                        cancellationToken: transactionCt);
                if (fenced.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_COMPENSATION_FENCE_CONFLICT");
                }
                var activeWorkers =
                    await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
                        session,
                        x =>
                            x.FlowInstanceId == flowInstanceId &&
                            x.Status ==
                            DynamicFlowRuntimeOutboxStatuses.Processing &&
                            x.LeaseUntilUtc > transactionNow,
                        cancellationToken: transactionCt);
                if (activeWorkers != 0)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_COMPENSATION_WORKER_ACTIVE");
                }
                foreach (var assignmentId in assignmentIds)
                {
                    var lease = reconcileLeases.SingleOrDefault(item =>
                        item.AssignmentId == assignmentId);
                    if (lease is null)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_COMPENSATION_ASSIGNMENT_LEASE_MISSING");
                    }
                    var leaseHeld =
                        await _ctx.WorkAssignments.CountDocumentsAsync(
                            session,
                            x =>
                                x.Id == assignmentId &&
                                x.ReportLifecycleLeaseId ==
                                lease.LeaseId &&
                                x.ReportLifecycleLeaseExpiresAtUtc >
                                transactionNow &&
                                !x.IsDeleted,
                            cancellationToken: transactionCt);
                    if (leaseHeld != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_COMPENSATION_ASSIGNMENT_LEASE_LOST");
                    }
                }
                await _ctx.WorkAssignments.UpdateManyAsync(
                    session,
                    x =>
                        assignmentIds.Contains(x.Id) &&
                        x.FlowInstanceId == flowInstanceId,
                    Builders<WorkAssignment>.Update
                        .Set(x => x.IsActive, false)
                        .Set(x => x.UpdatedAtUtc, transactionNow)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                    session,
                    x =>
                        (assignmentIds.Contains(
                             x.WorkAssignmentId) ||
                         (bindingClaimIds.Contains(x.Id) &&
                          x.WorkId == instance.WorkId)) &&
                        x.WorkId == instance.WorkId,
                    Builders<WorkTemplateAssignee>.Update
                        .Set(x => x.IsActive, false)
                        .Set(x => x.UpdatedAtUtc, transactionNow)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                await _ctx.WorkReportPeriods.UpdateManyAsync(
                    session,
                    x =>
                        x.WorkId == instance.WorkId &&
                        assignmentIds.Contains(
                            x.WorkAssignmentId),
                    Builders<WorkReportPeriod>.Update
                        .Set(x => x.IsActive, false)
                        .Set(x => x.UpdatedAtUtc, transactionNow)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
                    session,
                    x =>
                        x.WorkId == instance.WorkId &&
                        assignmentIds.Contains(
                            x.WorkAssignmentId),
                    Builders<WorkAssignmentQueueItem>.Update
                        .Set(x => x.IsActive, false)
                        .Set(x => x.UpdatedAtUtc, transactionNow)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    instance.WorkId,
                    transactionCt);
            },
            ct);
        _faults.ThrowIfConfigured(
            instance.LaunchCommandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterCompensationWrite);
        _faults.ThrowIfConfigured(
            instance.LaunchCommandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeCompensationProjection);
        foreach (var assignmentId in assignmentIds)
        {
            await _docRoles.DeleteDocRolesAsync(
                DocType.WORK_ASSIGNMENT,
                assignmentId,
                actorUserId,
                ct);
            await _projection.RebuildAssignmentAsync(assignmentId, actorUserId, ct);
        }
        await _docRoles.RebuildWorkParticipantRolesFromAssignmentsAsync(
            instance.WorkId,
            actorUserId,
            ct);
        foreach (var periodId in periodIds.Distinct(StringComparer.Ordinal))
            await _projection.RebuildReportPeriodAsync(periodId, actorUserId, ct);
        await CompleteCompensationAsync(
            flowInstanceId,
            actorUserId,
            runtimeReconcileLeaseId!,
            ct);
        _faults.ThrowIfConfigured(
            instance.LaunchCommandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterCompensationProjection);
        return Math.Max(assignmentIds.Count, bindingClaimIds.Count);
    }

    private async Task<bool> HasActiveCompensationArtifactsAsync(
        DynamicFlowInstance instance,
        CancellationToken ct)
    {
        var snapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct);
        var steps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == instance.Id &&
                !x.IsDeleted)
            .ToListAsync(ct);
        if (snapshot is null ||
            !TryBuildOwnershipProof(
                instance,
                snapshot,
                steps,
                out var ownershipProof))
        {
            return true;
        }
        var assignmentIds = ownershipProof
            .Select(branch => branch.AssignmentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (assignmentIds.Count == 0)
            return false;
        var periodRows = await _ctx.WorkReportPeriods
            .Find(x =>
                assignmentIds.Contains(x.WorkAssignmentId) &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var periodIds = periodRows
            .Select(period => period.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (await _ctx.WorkAssignments.CountDocumentsAsync(
                x =>
                    assignmentIds.Contains(x.Id) &&
                    x.IsActive &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0 ||
            await _ctx.WorkTemplateAssignees.CountDocumentsAsync(
                x =>
                    assignmentIds.Contains(x.WorkAssignmentId) &&
                    x.IsActive &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0 ||
            periodRows.Any(period => period.IsActive) ||
            await _ctx.WorkAssignmentQueueItems.CountDocumentsAsync(
                x =>
                    assignmentIds.Contains(x.WorkAssignmentId) &&
                    x.IsActive &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0 ||
            await _ctx.DocRoles.CountDocumentsAsync(
                x =>
                    x.DocType == DocType.WORK_ASSIGNMENT &&
                    assignmentIds.Contains(x.DocId) &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0 ||
            await _ctx.AssignmentListDocRoles.CountDocumentsAsync(
                x =>
                    assignmentIds.Contains(x.AssignmentId) &&
                    x.IsActive &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0 ||
            (periodIds.Count > 0 &&
             await _ctx.MyReportPeriodListDocRoles.CountDocumentsAsync(
                 x =>
                     periodIds.Contains(x.WorkReportPeriodId) &&
                     !x.IsDeleted,
                 cancellationToken: ct) > 0) ||
            await _ctx.ReviewReportListDocRoles.CountDocumentsAsync(
                x =>
                    assignmentIds.Contains(x.AssignmentId) &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0 ||
            await _ctx.ReviewAssignmentSummaryDocRoles.CountDocumentsAsync(
                x =>
                    assignmentIds.Contains(x.AssignmentId) &&
                    !x.IsDeleted,
                cancellationToken: ct) > 0)
        {
            return true;
        }

        var ownedTemplateKeys = periodRows
            .Where(period =>
                !string.IsNullOrWhiteSpace(
                    period.DynamicFormTemplateId))
            .Select(period =>
                $"{period.WorkId}\n{period.DynamicFormTemplateId}\n{period.AssigneeUserId}")
            .ToHashSet(StringComparer.Ordinal);
        if (ownedTemplateKeys.Count == 0)
            return false;
        var otherActivePeriods = await _ctx.WorkReportPeriods
            .Find(period =>
                period.WorkId == instance.WorkId &&
                !assignmentIds.Contains(period.WorkAssignmentId) &&
                period.IsActive &&
                !period.IsDeleted)
            .ToListAsync(ct);
        var sharedTemplateKeys = otherActivePeriods
            .Where(period =>
                !string.IsNullOrWhiteSpace(
                    period.DynamicFormTemplateId))
            .Select(period =>
                $"{period.WorkId}\n{period.DynamicFormTemplateId}\n{period.AssigneeUserId}")
            .ToHashSet(StringComparer.Ordinal);
        var orphanTemplateKeys = ownedTemplateKeys
            .Where(key => !sharedTemplateKeys.Contains(key))
            .ToHashSet(StringComparer.Ordinal);
        if (orphanTemplateKeys.Count == 0)
            return false;
        var templateRows = await _ctx.MyReportTemplateListDocRoles
            .Find(x =>
                x.WorkId == instance.WorkId &&
                !x.IsDeleted)
            .ToListAsync(ct);
        return templateRows.Any(row =>
            orphanTemplateKeys.Contains(
                $"{row.WorkId}\n{row.DynamicFormTemplateId}\n{row.UserId}"));
    }

    private async Task TryCompensateFailedInstanceAsync(
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct)
    {
        var failed = await _ctx.DynamicFlowInstances.CountDocumentsAsync(
            x =>
                x.Id == flowInstanceId &&
                x.State == DynamicFlowInstanceStates.Failed &&
                !x.IsDeleted,
            cancellationToken: ct);
        if (failed != 1)
            return;
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (instance is null)
            return;
        if (instance.CompensatedAtUtc.HasValue &&
            !await HasActiveCompensationArtifactsAsync(instance, ct))
        {
            await _ctx.DynamicFlowInstances.UpdateOneAsync(
                x =>
                    x.Id == flowInstanceId &&
                    x.State == DynamicFlowInstanceStates.Failed &&
                    x.CompensatedAtUtc != null &&
                    !x.IsDeleted,
                Builders<DynamicFlowInstance>.Update.Set(
                    x => x.CompensationCheckedAtUtc,
                    DateTime.UtcNow),
                cancellationToken: ct);
            return;
        }
        var activeWorkers = await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
            x =>
                x.FlowInstanceId == flowInstanceId &&
                x.Status == DynamicFlowRuntimeOutboxStatuses.Processing &&
                x.LeaseUntilUtc > DateTime.UtcNow,
            cancellationToken: ct);
        if (activeWorkers != 0)
            return;
        try
        {
            await CompensateOwnedArtifactsAsync(flowInstanceId, true, actorUserId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(
                error,
                "Dynamic Flow runtime compensation failed and remains retryable. instanceId={instanceId}",
                flowInstanceId);
        }
    }

    private Task CompleteCompensationAsync(
        string flowInstanceId,
        string actorUserId,
        string runtimeReconcileLeaseId,
        CancellationToken ct)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var activeWorkers =
                    await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
                        session,
                        x =>
                            x.FlowInstanceId == flowInstanceId &&
                            x.Status ==
                            DynamicFlowRuntimeOutboxStatuses.Processing &&
                            x.LeaseUntilUtc > now,
                        cancellationToken: transactionCt);
                if (activeWorkers != 0)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_COMPENSATION_WORKER_ACTIVE");
                }
                var instance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeCompensationFenceFilter(
                            flowInstanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.CompensatedAtUtc,
                            null))
                    .FirstOrDefaultAsync(transactionCt);
                if (instance is null)
                {
                    var completed = await _ctx.DynamicFlowInstances.CountDocumentsAsync(
                        session,
                        RuntimeCompensationFenceFilter(
                            flowInstanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        Builders<DynamicFlowInstance>.Filter.Ne(
                            x => x.CompensatedAtUtc,
                            null),
                        cancellationToken: transactionCt);
                    if (completed == 1)
                    {
                        await _ctx.DynamicFlowInstances.UpdateOneAsync(
                            session,
                            RuntimeCompensationFenceFilter(
                                flowInstanceId,
                                runtimeReconcileLeaseId,
                                now) &
                            Builders<DynamicFlowInstance>.Filter.Ne(
                                x => x.CompensatedAtUtc,
                                null),
                            Builders<DynamicFlowInstance>.Update.Set(
                                x => x.CompensationCheckedAtUtc,
                                now),
                            cancellationToken: transactionCt);
                        return;
                    }
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_COMPENSATION_STATE_CONFLICT");
                }

                var completedInstance = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    RuntimeCompensationFenceFilter(
                        flowInstanceId,
                        runtimeReconcileLeaseId,
                        now) &
                    Builders<DynamicFlowInstance>.Filter.Eq(
                        x => x.CompensatedAtUtc,
                        null) &
                    Builders<DynamicFlowInstance>.Filter.Eq(
                        x => x.Revision,
                        instance.Revision),
                    Builders<DynamicFlowInstance>.Update
                        .Set(x => x.CompensatedAtUtc, now)
                        .Set(x => x.CompensationCheckedAtUtc, now)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Inc(
                            x =>
                                x.RuntimeMaterializationFenceRevision,
                            1)
                        .Inc(x => x.NextEventSequence, 1)
                        .Inc(x => x.Revision, 1),
                    cancellationToken: transactionCt);
                if (completedInstance.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_COMPENSATION_STATE_CONFLICT");
                }

                var sequence = instance.NextEventSequence;
                var eventId = StableObjectId(
                    $"{flowInstanceId}\nevent\ncompensation-applied");
                var payload = new BsonDocument("state", DynamicFlowInstanceStates.Failed);
                await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeEvent
                    {
                        Id = eventId,
                        FlowInstanceId = flowInstanceId,
                        StepInstanceId = null,
                        Sequence = sequence,
                        EventType = DynamicFlowRuntimeEventTypes.CompensationApplied,
                        CommandId = instance.LaunchCommandId,
                        ActorUserId = actorUserId,
                        VisibleUnitIds = new List<string> { instance.IssuerUnitId },
                        Payload = payload,
                        PayloadHash = Hash(payload.ToJson()),
                        OccurredAtUtc = now
                    },
                    cancellationToken: transactionCt);
            },
            ct);

    private async Task ProcessInstanceAsync(
        string flowInstanceId,
        int maxItems,
        CancellationToken ct,
        IReadOnlyList<WorkReportLifecycleSeriesLease>? reconcileLeases = null,
        string? runtimeReconcileLeaseId = null)
    {
        for (var index = 0; index < maxItems; index++)
        {
            if (reconcileLeases is not null)
                await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            var item = await ClaimNextAsync(
                flowInstanceId,
                ct,
                runtimeReconcileLeaseId);
            if (item is null)
                break;
            if (reconcileLeases is not null)
                await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
            await ProcessClaimedAsync(item, ct, runtimeReconcileLeaseId);
        }

        if (reconcileLeases is not null)
            await RenewRuntimeReconcileLeasesAsync(reconcileLeases, ct);
        if (string.IsNullOrWhiteSpace(runtimeReconcileLeaseId))
            await TryFinalizeInstanceAsync(flowInstanceId, ct);
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId)
            .Project(x => new { x.State, x.IssuerUserId })
            .FirstOrDefaultAsync(ct);
        if (instance?.State == DynamicFlowInstanceStates.Failed)
            await TryCompensateFailedInstanceAsync(flowInstanceId, instance.IssuerUserId, ct);
    }

    private async Task<bool> PreparePendingReplayAsync(
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct)
    {
        var commandId = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .Project(x => x.LaunchCommandId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeRetryStateWrite);
        var recovery = await _transactions.ExecuteAsync<bool?>(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var instance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeMaterializationFenceFilter(
                            flowInstanceId,
                            null,
                            now))
                    .FirstOrDefaultAsync(transactionCt);
                if (instance is null)
                    return null;

                var liveClaimCount =
                    await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
                        session,
                        x =>
                            x.FlowInstanceId == flowInstanceId &&
                            x.Status ==
                            DynamicFlowRuntimeOutboxStatuses.Processing &&
                            x.LeaseUntilUtc > now,
                        cancellationToken: transactionCt);
                if (liveClaimCount != 0)
                    return null;

                var isRecovery =
                    instance.State is DynamicFlowInstanceStates.Partial
                        or DynamicFlowInstanceStates.Retrying;
                var sourceMembershipChanged =
                    instance.State == DynamicFlowInstanceStates.Partial;
                var recoveryEpoch = instance.RuntimeRecoveryEpoch +
                                    (instance.State ==
                                     DynamicFlowInstanceStates.Partial
                                        ? 1
                                        : 0);
                var recoveryEventId = RecoveryEventId(
                    flowInstanceId,
                    DynamicFlowRuntimeEventTypes.RecoveryStarted,
                    recoveryEpoch);
                var recoveryEventExists = isRecovery &&
                    await _ctx.DynamicFlowRuntimeEvents.CountDocumentsAsync(
                        session,
                        x => x.Id == recoveryEventId,
                        cancellationToken: transactionCt) > 0;

                var instanceUpdate =
                    Builders<DynamicFlowInstance>.Update.Inc(
                        x => x.RuntimeMaterializationFenceRevision,
                        1);
                if (instance.State == DynamicFlowInstanceStates.Partial)
                {
                    instanceUpdate = instanceUpdate
                        .Set(x => x.State, DynamicFlowInstanceStates.Retrying)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Inc(x => x.Revision, 1);
                    if (isRecovery)
                    {
                        instanceUpdate = instanceUpdate.Inc(
                            x => x.RuntimeRecoveryEpoch,
                            1);
                    }
                }
                if (isRecovery && !recoveryEventExists)
                {
                    instanceUpdate = instanceUpdate.Inc(
                        x => x.NextEventSequence,
                        1);
                }
                var fenced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    RuntimeMaterializationFenceFilter(
                        flowInstanceId,
                        null,
                        now) &
                    Builders<DynamicFlowInstance>.Filter.Eq(
                        x => x.Revision,
                        instance.Revision),
                    instanceUpdate,
                    cancellationToken: transactionCt);
                if (fenced.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_REPLAY_FENCE_CONFLICT");
                }

                if (instance.State == DynamicFlowInstanceStates.Partial)
                {
                    await _ctx.DynamicFlowStepInstances.UpdateManyAsync(
                        session,
                        x =>
                            x.FlowInstanceId == flowInstanceId &&
                            x.State == DynamicFlowStepStates.Partial,
                        Builders<DynamicFlowStepInstance>.Update
                            .Set(x => x.State, DynamicFlowStepStates.Retrying)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1),
                        cancellationToken: transactionCt);
                }
                if (isRecovery && !recoveryEventExists)
                {
                    _faults.ThrowIfConfigured(
                        commandId,
                        0,
                        DynamicFlowRuntimeFaultPoints.BeforeRecoveryEventWrite);
                    instance.State = DynamicFlowInstanceStates.Retrying;
                    instance.RuntimeRecoveryEpoch = recoveryEpoch;
                    await UpsertRecoveryEventAsync(
                        session,
                        instance,
                        DynamicFlowRuntimeEventTypes.RecoveryStarted,
                        actorUserId,
                        instance.NextEventSequence,
                        DynamicFlowInstanceStates.Retrying,
                        now,
                        transactionCt);
                }
                await _ctx.DynamicFlowRuntimeOutbox.UpdateManyAsync(
                    session,
                    x =>
                        x.FlowInstanceId == flowInstanceId &&
                        x.Status == DynamicFlowRuntimeOutboxStatuses.Failed,
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(x => x.NextAttemptAtUtc, now)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                if (sourceMembershipChanged)
                {
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        instance.WorkId,
                        transactionCt);
                }
                return isRecovery;
            },
            ct);
        if (!recovery.HasValue)
            return false;
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterRetryStateWrite);
        if (recovery.Value)
        {
            _faults.ThrowIfConfigured(
                commandId,
                0,
                DynamicFlowRuntimeFaultPoints.AfterRecoveryEventWrite);
        }
        return recovery.Value;
    }

    private async Task<bool> CanExecuteStoredInstanceAsync(
        string flowInstanceId,
        CancellationToken ct)
    {
        var pin = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == flowInstanceId &&
                !candidate.IsDeleted)
            .Project(candidate => new
            {
                candidate.CatalogVersion,
                candidate.CatalogSemanticHash,
                candidate.ArchetypeId
            })
            .FirstOrDefaultAsync(ct);
        if (pin is null)
            return false;
        return CanExecuteRuntimePin(
            pin.CatalogVersion,
            pin.CatalogSemanticHash,
            pin.ArchetypeId);
    }

    private bool CanExecuteRuntimePin(
        string? catalogVersion,
        string? catalogSemanticHash,
        string? archetypeId)
    {
        if (DynamicFlowRuntimeEligibilityPolicy.IsCurrentCatalog(
                catalogVersion,
                catalogSemanticHash))
        {
            return archetypeId is "FLOW-T01" or "FLOW-T02";
        }

        return _runtimeActivation.CanExecuteP6CandidatePin(
            catalogVersion,
            catalogSemanticHash,
            archetypeId);
    }

    private async Task<DynamicFlowRuntimeOutboxItem?> ClaimNextAsync(
        string? flowInstanceId,
        CancellationToken ct,
        string? runtimeReconcileLeaseId = null)
    {
        var now = DateTime.UtcNow;
        var fb = Builders<DynamicFlowRuntimeOutboxItem>.Filter;
        var runnable =
            fb.Eq(x => x.Status, DynamicFlowRuntimeOutboxStatuses.Pending) |
            (fb.Eq(x => x.Status, DynamicFlowRuntimeOutboxStatuses.Failed) &
             fb.Lte(x => x.NextAttemptAtUtc, now)) |
            (fb.Eq(x => x.Status, DynamicFlowRuntimeOutboxStatuses.Processing) &
             (fb.Lt(x => x.LeaseUntilUtc, now) | fb.Eq(x => x.LeaseUntilUtc, null)));
        var filter = runnable;
        if (!string.IsNullOrWhiteSpace(flowInstanceId))
            filter &= fb.Eq(x => x.FlowInstanceId, flowInstanceId);

        const int candidatePageSize = 20;
        for (var candidateOffset = 0;; candidateOffset += candidatePageSize)
        {
            var candidates = await _ctx.DynamicFlowRuntimeOutbox
                .Find(filter)
                .SortBy(x => x.NextAttemptAtUtc)
                .ThenBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .Skip(candidateOffset)
                .Limit(candidatePageSize)
                .Project(x => new { x.Id, x.FlowInstanceId })
                .ToListAsync(ct);
            if (candidates.Count == 0)
                return null;
            foreach (var candidate in candidates)
            {
                if (!await CanExecuteStoredInstanceAsync(
                        candidate.FlowInstanceId,
                        ct))
                {
                    continue;
                }

                var claimed =
                    await _transactions.ExecuteAsync<DynamicFlowRuntimeOutboxItem?>(
                        async (session, transactionCt) =>
                        {
                            var claimNow = DateTime.UtcNow;
                            var fenced =
                                await _ctx.DynamicFlowInstances.UpdateOneAsync(
                                    session,
                                    RuntimeMaterializationFenceFilter(
                                        candidate.FlowInstanceId,
                                        runtimeReconcileLeaseId,
                                        claimNow),
                                    Builders<DynamicFlowInstance>.Update.Inc(
                                        x =>
                                            x.RuntimeMaterializationFenceRevision,
                                        1),
                                    cancellationToken: transactionCt);
                            if (fenced.ModifiedCount != 1)
                                return null;
                            return await _ctx.DynamicFlowRuntimeOutbox
                                .FindOneAndUpdateAsync(
                                    session,
                                    filter & fb.Eq(x => x.Id, candidate.Id),
                                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                                        .Set(
                                            x => x.Status,
                                            DynamicFlowRuntimeOutboxStatuses.Processing)
                                        .Set(
                                            x => x.LeaseId,
                                            ObjectId.GenerateNewId().ToString())
                                        .Set(
                                            x => x.LeaseUntilUtc,
                                            claimNow.Add(LeaseDuration))
                                        .Set(x => x.UpdatedAtUtc, claimNow),
                                    new FindOneAndUpdateOptions<
                                        DynamicFlowRuntimeOutboxItem>
                                    {
                                        ReturnDocument = ReturnDocument.After
                                    },
                                    transactionCt);
                        },
                        ct);
                if (claimed is not null)
                    return claimed;
            }
            if (candidates.Count < candidatePageSize)
                return null;
        }
    }

    private async Task ProcessClaimedAsync(
        DynamicFlowRuntimeOutboxItem item,
        CancellationToken ct,
        string? runtimeReconcileLeaseId = null)
    {
        var commandId =
            item.Payload.TryGetValue("commandId", out var commandIdValue) &&
            commandIdValue.IsString
                ? commandIdValue.AsString
                : string.Empty;
        var branchOrdinal =
            item.Payload.TryGetValue("branchOrdinal", out var branchOrdinalValue) &&
            branchOrdinalValue.IsInt32
                ? branchOrdinalValue.AsInt32
                : 0;
        _faults.ThrowIfConfigured(
            commandId,
            branchOrdinal,
            DynamicFlowRuntimeFaultPoints.AfterOutboxClaim);

        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                operationCancellation.Token);
        var heartbeatFailure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatTask = RunMaterializationClaimHeartbeatAsync(
            item,
            runtimeReconcileLeaseId,
            heartbeatCancellation,
            operationCancellation,
            heartbeatFailure);
        try
        {
            await ProcessClaimedCoreAsync(
                item,
                operationCancellation.Token,
                runtimeReconcileLeaseId);
        }
        catch (OperationCanceledException) when (
            heartbeatFailure.Task.IsCompletedSuccessfully)
        {
            await ReleaseClaimForRuntimeReconcileAsync(
                item,
                CancellationToken.None);
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_HEARTBEAT_FAILED",
                await heartbeatFailure.Task);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping the per-claim heartbeat.
            }
        }
    }

    private async Task ProcessClaimedCoreAsync(
        DynamicFlowRuntimeOutboxItem item,
        CancellationToken ct,
        string? runtimeReconcileLeaseId)
    {
        try
        {
            await EnsureClaimRunnableAsync(item, runtimeReconcileLeaseId, ct);
            if (item.AttemptCount > 0)
            {
                await EnsureRetryingAsync(
                    item.FlowInstanceId,
                    item.Payload["actorUserId"].AsString,
                    ct,
                    runtimeReconcileLeaseId);
            }
            if (item.Operation is not (
                    DynamicFlowRuntimeMaterializationOperations.MaterializeEntryAssignment or
                    DynamicFlowRuntimeMaterializationOperations.MaterializeSequentialAssignment or
                    DynamicFlowRuntimeMaterializationOperations.MaterializeForkBranchAssignment or
                    DynamicFlowRuntimeMaterializationOperations.MaterializeReviewAttemptAssignment or
                    DynamicFlowRuntimeMaterializationOperations.MaterializeSupplementalAssignment))
            {
                throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_OUTBOX_OPERATION_UNKNOWN");
            }

            try
            {
                await MaterializeEntryAssignmentAsync(
                    item,
                    runtimeReconcileLeaseId,
                    ct);
            }
            catch (DynamicFlowRuntimeAssignmentTerminalException terminal)
            {
                var actorUserId = item.Payload.TryGetValue(
                    "actorUserId",
                    out var actor)
                    ? actor.AsString
                    : string.Empty;
                await _queue.DisableByAssignmentAsync(
                    terminal.AssignmentId,
                    actorUserId,
                    ct);
            }
            await CompleteOutboxAsync(item, runtimeReconcileLeaseId, ct);
        }
        catch (DynamicFlowRuntimeReconcileFenceException)
        {
            await ReleaseClaimForRuntimeReconcileAsync(item, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try
            {
                await MarkOutboxFailureAsync(
                    item,
                    error,
                    ct,
                    runtimeReconcileLeaseId);
            }
            catch (DynamicFlowRuntimeReconcileFenceException)
            {
                await ReleaseClaimForRuntimeReconcileAsync(item, ct);
                return;
            }
            var issuerUserId = item.Payload.TryGetValue("actorUserId", out var actor)
                ? actor.AsString
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(issuerUserId))
                await TryCompensateFailedInstanceAsync(item.FlowInstanceId, issuerUserId, ct);
            _logger.LogWarning(
                error,
                "Dynamic Flow runtime outbox item failed. itemId={itemId} instanceId={instanceId} attempt={attempt}",
                item.Id,
                item.FlowInstanceId,
                item.AttemptCount + 1);
        }
    }

    private async Task RunMaterializationClaimHeartbeatAsync(
        DynamicFlowRuntimeOutboxItem item,
        string? runtimeReconcileLeaseId,
        CancellationTokenSource heartbeatCancellation,
        CancellationTokenSource operationCancellation,
        TaskCompletionSource<Exception> failure)
    {
        try
        {
            while (true)
            {
                await Task.Delay(
                    MaterializationHeartbeatInterval,
                    heartbeatCancellation.Token);
                var renewed = await RenewMaterializationClaimLeaseAsync(
                    item,
                    runtimeReconcileLeaseId,
                    heartbeatCancellation.Token);
                if (!renewed)
                    return;
            }
        }
        catch (OperationCanceledException) when (
            heartbeatCancellation.IsCancellationRequested)
        {
            // Normal claim completion or caller cancellation.
        }
        catch (Exception error)
        {
            failure.TrySetResult(error);
            operationCancellation.Cancel();
        }
    }

    private Task<bool> RenewMaterializationClaimLeaseAsync(
        DynamicFlowRuntimeOutboxItem item,
        string? runtimeReconcileLeaseId,
        CancellationToken ct)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var held = await _ctx.DynamicFlowRuntimeOutbox
                    .CountDocumentsAsync(
                        session,
                        HeldClaim(item),
                        cancellationToken: transactionCt);
                if (held != 1)
                {
                    var stillOwned = await _ctx.DynamicFlowRuntimeOutbox
                        .CountDocumentsAsync(
                            session,
                            x =>
                                x.Id == item.Id &&
                                x.Status ==
                                DynamicFlowRuntimeOutboxStatuses.Processing &&
                                x.LeaseId == item.LeaseId,
                            cancellationToken: transactionCt);
                    if (stillOwned == 0)
                        return false;
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_LOST");
                }

                var fenced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    RuntimeMaterializationFenceFilter(
                        item.FlowInstanceId,
                        runtimeReconcileLeaseId,
                        now),
                    Builders<DynamicFlowInstance>.Update.Inc(
                        x => x.RuntimeMaterializationFenceRevision,
                        1),
                    cancellationToken: transactionCt);
                if (fenced.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECONCILE_FENCE_HELD");
                }

                var renewed =
                    await _ctx.DynamicFlowRuntimeOutbox.UpdateOneAsync(
                        session,
                        HeldClaim(item),
                        Builders<DynamicFlowRuntimeOutboxItem>.Update
                            .Set(
                                x => x.LeaseUntilUtc,
                                now.Add(LeaseDuration))
                            .Set(x => x.UpdatedAtUtc, now),
                        cancellationToken: transactionCt);
                if (renewed.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_LOST");
                }
                return true;
            },
            ct);

    private async Task MaterializeEntryAssignmentAsync(
        DynamicFlowRuntimeOutboxItem item,
        string? runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        await EnsureClaimRunnableAsync(item, runtimeReconcileLeaseId, ct);
        if (!string.Equals(item.PayloadHash, Hash(item.Payload.ToJson()), StringComparison.Ordinal))
            throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_OUTBOX_PAYLOAD_TAMPERED");
        var archetypeId = item.Payload["archetypeId"].AsString;
        if (archetypeId is not (
                "FLOW-T01" or
                "FLOW-T02" or
                "FLOW-T03" or
                "FLOW-T04" or
                "FLOW-T05" or
                "FLOW-T06" or
                "FLOW-T07" or
                "FLOW-T08" or
                "FLOW-T09" or
                "FLOW-T10" or
                "FLOW-T11" or
                "FLOW-T12"))
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_ARCHETYPE_BLOCKED_UNTIL_TARGET_PROMPT");
        var ordinal = item.Payload["branchOrdinal"].AsInt32;
        var commandId = item.Payload["commandId"].AsString;
        var assignmentId = item.Payload["assignmentId"].AsString;
        var step = await _ctx.DynamicFlowStepInstances
            .Find(x => x.Id == item.StepInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_STEP_INSTANCE_NOT_FOUND");
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == item.FlowInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        var participantSnapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_MISSING");
        if (!string.Equals(
                participantSnapshot.SnapshotHash,
                ParticipantSnapshotHash(participantSnapshot),
                StringComparison.Ordinal) ||
            !string.Equals(
                participantSnapshot.SnapshotHash,
                instance.ParticipantSnapshotHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_TAMPERED");
        }
        await RequireMaterializationIntentPinsAsync(
            item,
            step,
            instance,
            participantSnapshot,
            ct);
        var durableBinding = participantSnapshot.Bindings.SingleOrDefault(binding =>
                                 binding.TargetUnitId == step.TargetUnitId)
                             ?? throw new InvalidOperationException(
                                 "DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_BINDING_MISSING");
        var durableParticipants = new BsonArray(durableBinding.Participants
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .Select(ParticipantDocument));
        if (!string.Equals(
                durableParticipants.ToJson(),
                item.Payload["participants"].AsBsonArray.ToJson(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_DRIFT");
        }
        var form = await _ctx.DynamicFormTemplates
            .Find(x =>
                x.Id == step.FormVersionId &&
                x.IsActive &&
                x.IsPublished &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_FORM_PIN_UNAVAILABLE");
        var publishedSnapshot = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
        var formFamilyId = string.IsNullOrWhiteSpace(form.FamilyId) ? form.Id : form.FamilyId;
        if (!string.Equals(formFamilyId, step.FormFamilyId, StringComparison.Ordinal) ||
            Math.Max(1, form.VersionNo) != step.FormVersionNo ||
            !string.Equals(publishedSnapshot.Sha256, step.FormSchemaHash, StringComparison.Ordinal) ||
            !string.Equals(publishedSnapshot.Sha256, step.FormSnapshotHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_FORM_PIN_STALE");
        }

        var assignees = FrozenUsers(item.Payload["participants"].AsBsonArray);
        if (assignees.Count == 0 ||
            assignees.Any(user => !string.Equals(user.UnitId, step.TargetUnitId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_DRIFT");
        }

        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.BeforeAssignmentWrite);
        var assignment = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                await EnsureClaimRunnableAsync(
                    session,
                    item,
                    runtimeReconcileLeaseId,
                    transactionCt);
                var materializedAssignment =
                    await UpsertOwnedAssignmentAsync(
                    session,
                    instance,
                    step,
                    form,
                    assignees,
                    assignmentId,
                    transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    instance.WorkId,
                    transactionCt);
                return materializedAssignment;
            },
            ct);
        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite);

        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.BeforeBindingWrite);
        var bindings = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                await EnsureClaimRunnableAsync(
                    session,
                    item,
                    runtimeReconcileLeaseId,
                    transactionCt);
                await FenceOwnedAssignmentAsync(
                    session,
                    assignment.Id,
                    instance.Id,
                    transactionCt);
                return await UpsertBindingsAsync(
                    session,
                    assignment,
                    form,
                    assignees,
                    transactionCt);
            },
            ct);
        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.AfterBindingWrite);

        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.BeforeDocRoleProjection);
        await EnsureClaimRunnableAsync(item, runtimeReconcileLeaseId, ct);
        await _docRoles.UpsertWorkAssignmentRolesAsync(assignment, ct);
        await _docRoles.RebuildWorkParticipantRolesFromAssignmentsAsync(
            assignment.WorkId,
            instance.IssuerUserId,
            ct);
        await _projection.RebuildAssignmentAsync(assignment.Id, instance.IssuerUserId, ct);
        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.AfterDocRoleProjection);

        var materializedPeriods = new List<WorkReportPeriod>();
        foreach (var binding in bindings)
        {
            _faults.ThrowIfConfigured(
                commandId,
                ordinal,
                DynamicFlowRuntimeFaultPoints.BeforePeriodWrite);
            var period = await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    await EnsureClaimRunnableAsync(
                        session,
                        item,
                        runtimeReconcileLeaseId,
                        transactionCt);
                    var currentAssignment = await FenceOwnedAssignmentAsync(
                        session,
                        assignment.Id,
                        instance.Id,
                        transactionCt);
                    var materializedPeriod =
                        await UpsertLaunchPeriodAsync(
                        session,
                        instance,
                        currentAssignment,
                        binding,
                        transactionCt);
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        instance.WorkId,
                        transactionCt);
                    return materializedPeriod;
                },
                ct);
            materializedPeriods.Add(period);
            _faults.ThrowIfConfigured(
                commandId,
                ordinal,
                DynamicFlowRuntimeFaultPoints.AfterPeriodWrite);

            _faults.ThrowIfConfigured(
                commandId,
                ordinal,
                DynamicFlowRuntimeFaultPoints.BeforeQueueWrite);
            await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    await EnsureClaimRunnableAsync(
                        session,
                        item,
                        runtimeReconcileLeaseId,
                        transactionCt);
                    var currentAssignment = await FenceOwnedAssignmentAsync(
                        session,
                        assignment.Id,
                        instance.Id,
                        transactionCt);
                    await UpsertRuntimeQueueAsync(
                        session,
                        period,
                        currentAssignment.CompletedAtUtc.HasValue,
                        instance.IssuerUserId,
                        transactionCt);
                },
                ct);
            await _projection.RebuildReportPeriodAsync(period.Id, instance.IssuerUserId, ct);
            _faults.ThrowIfConfigured(
                commandId,
                ordinal,
                DynamicFlowRuntimeFaultPoints.AfterQueueWrite);
        }

        var expectedPeriodIds = materializedPeriods.Select(period => period.Id).ToList();
        var expectedAssigneeIds = bindings.Select(binding => binding.AssigneeUserId).ToList();
        var stalePeriodIds = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                await EnsureClaimRunnableAsync(
                    session,
                    item,
                    runtimeReconcileLeaseId,
                    transactionCt);
                await FenceOwnedAssignmentAsync(
                    session,
                    assignment.Id,
                    instance.Id,
                    transactionCt);
                var ids = await _ctx.WorkReportPeriods
                    .Find(
                        session,
                        period =>
                            period.WorkAssignmentId == assignment.Id &&
                            !expectedPeriodIds.Contains(period.Id) &&
                            period.IsActive &&
                            !period.IsDeleted)
                    .Project(period => period.Id)
                    .ToListAsync(transactionCt);
                if (ids.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    await _ctx.WorkReportPeriods.UpdateManyAsync(
                        session,
                        period => ids.Contains(period.Id),
                        Builders<WorkReportPeriod>.Update
                            .Set(period => period.IsActive, false)
                            .Set(period => period.UpdatedAtUtc, now)
                            .Set(period => period.UpdatedByUserId, instance.IssuerUserId),
                        cancellationToken: transactionCt);
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        instance.WorkId,
                        transactionCt);
                }
                await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
                    session,
                    queue =>
                        queue.WorkAssignmentId == assignment.Id &&
                        (queue.PeriodKey != instance.PeriodKey ||
                         !expectedAssigneeIds.Contains(queue.AssigneeUserId)) &&
                        queue.IsActive &&
                        !queue.IsDeleted,
                    Builders<WorkAssignmentQueueItem>.Update
                        .Set(queue => queue.IsActive, false)
                        .Set(queue => queue.UpdatedAtUtc, DateTime.UtcNow)
                        .Set(queue => queue.UpdatedByUserId, instance.IssuerUserId),
                    cancellationToken: transactionCt);
                return ids;
            },
            ct);
        foreach (var stalePeriodId in stalePeriodIds)
            await _projection.RebuildReportPeriodAsync(
                stalePeriodId,
                instance.IssuerUserId,
                ct);

        // Period creation changes the canonical leaf progress projection. Keep the
        // assignment and its denormalized read model in the same converged state
        // before the outbox item is allowed to complete.
        await EnsureClaimRunnableAsync(item, runtimeReconcileLeaseId, ct);
        await _progress.RecomputeSingleAsync(assignment.Id, ct);
        await EnsureClaimRunnableAsync(item, runtimeReconcileLeaseId, ct);
        foreach (var materializedPeriod in materializedPeriods)
        {
            await _projection.RebuildReportPeriodAsync(
                materializedPeriod.Id,
                instance.IssuerUserId,
                ct);
            await EnsureClaimRunnableAsync(
                item,
                runtimeReconcileLeaseId,
                ct);
        }
        await _projection.RebuildAssignmentAsync(
            assignment.Id,
            instance.IssuerUserId,
            ct);
        await EnsureClaimRunnableAsync(item, runtimeReconcileLeaseId, ct);
    }

    private async Task RequireMaterializationIntentPinsAsync(
        DynamicFlowRuntimeOutboxItem item,
        DynamicFlowStepInstance step,
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        CancellationToken ct)
    {
        if (item.Id != StableObjectId($"{step.Id}\noutbox\nmaterialize") ||
            item.FlowInstanceId != instance.Id ||
            item.StepInstanceId != step.Id ||
            step.FlowInstanceId != instance.Id ||
            step.AssignmentId is null ||
            !TryReadString(item.Payload, "assignmentId", out var assignmentId) ||
            assignmentId != step.AssignmentId ||
            !TryReadString(item.Payload, "targetUnitId", out var targetUnitId) ||
            targetUnitId != step.TargetUnitId ||
            !TryReadString(item.Payload, "issuerUnitId", out var issuerUnitId) ||
            issuerUnitId != instance.IssuerUnitId ||
            !TryReadString(item.Payload, "archetypeId", out var archetypeId) ||
            archetypeId != instance.ArchetypeId ||
            !TryReadString(item.Payload, "periodKey", out var periodKey) ||
            periodKey != instance.PeriodKey ||
            !TryReadString(
                item.Payload,
                "scheduleIdentityJson",
                out var scheduleIdentityJson) ||
            !TryReadString(
                item.Payload,
                "scheduleIdentityHash",
                out var scheduleIdentityHash) ||
            scheduleIdentityJson != instance.ScheduleIdentityJson ||
            !FixedEquals(
                scheduleIdentityHash,
                instance.ScheduleIdentityHash) ||
            !FixedEquals(
                Hash(scheduleIdentityJson),
                instance.ScheduleIdentityHash))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var isEntry =
            string.Equals(
                step.FlowStepId,
                instance.EntryFlowStepId,
                StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(step.ActivatedByTransitionId) &&
            step.AttemptNo == 1;
        if (step.IsSupplemental)
        {
            if (instance.ArchetypeId !=
                    DynamicFlowSupplementalTopologyContract.ArchetypeId ||
                item.Operation !=
                    DynamicFlowRuntimeMaterializationOperations
                        .MaterializeSupplementalAssignment ||
                step.ExecutionEpoch != instance.ExecutionEpoch ||
                step.SupplementalStepId != step.Id ||
                step.RequestedByUserId != instance.IssuerUserId ||
                !TryReadString(
                    item.Payload,
                    "commandId",
                    out var supplementalCommandId) ||
                !TryReadString(
                    item.Payload,
                    "supplementalStepId",
                    out var supplementalStepId) ||
                supplementalStepId != step.Id ||
                item.DedupeKey !=
                    $"{instance.Id}:{instance.ExecutionEpoch}:{step.Id}:supplemental:materialize")
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
            }
            return;
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowFinalizeTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            await RequireFinalizeMaterializationIntentPinsAsync(
                item,
                step,
                instance,
                snapshot,
                ct);
            return;
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowReviewLoopTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            await RequireReviewLoopMaterializationIntentPinsAsync(
                item,
                step,
                instance,
                snapshot,
                isEntry,
                ct);
            return;
        }
        if (IsParallelFanOutArchetype(instance.ArchetypeId))
        {
            await RequireParallelForkMaterializationIntentPinsAsync(
                item,
                step,
                instance,
                snapshot,
                isEntry,
                ct);
            return;
        }
        if (!string.Equals(
                instance.ArchetypeId,
                DynamicFlowSequentialTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            if (!isEntry ||
                item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeEntryAssignment ||
                !TryReadString(item.Payload, "commandId", out var launchCommandId) ||
                launchCommandId != instance.LaunchCommandId ||
                !TryReadString(
                    item.Payload,
                    "actorUserId",
                    out var launchActorUserId) ||
                launchActorUserId != instance.IssuerUserId ||
                item.DedupeKey !=
                $"{instance.Id}:{step.FlowStepId}:{step.TargetUnitId}:{step.AttemptNo}:materialize")
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
            }
            return;
        }

        var topology = DynamicFlowSequentialTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var expectedBranchId =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var expectedStepId =
            DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                step.FlowStepId,
                expectedBranchId,
                step.AttemptNo);
        var expectedAssignmentId =
            DynamicFlowSequentialTopologyContract.BuildAssignmentId(
                expectedStepId);
        var binding = snapshot.Bindings.SingleOrDefault(candidate =>
            candidate.TargetUnitId == step.TargetUnitId);
        if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                instance.CatalogVersion,
                instance.CatalogSemanticHash) ||
            instance.DefinitionRevision !=
            DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                instance.FlowTemplateVersionId,
                instance.FlowTemplateVersionNo,
                instance.FlowPayloadHash) ||
            instance.TopologySnapshotHash != instance.FlowPayloadHash ||
            !SequentialStepPinsMatch(instance, topology, step) ||
            step.ExecutionEpoch != instance.ExecutionEpoch ||
            step.AttemptNo != 1 ||
            step.BranchId != expectedBranchId ||
            step.Id != expectedStepId ||
            step.AssignmentId != expectedAssignmentId ||
            step.ParticipantSnapshotId != snapshot.Id ||
            binding is null ||
            !binding.AssigneeUserIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    step.ParticipantUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal)) ||
            !TryReadInt32(
                item.Payload,
                "executionEpoch",
                out var executionEpoch) ||
            executionEpoch != step.ExecutionEpoch ||
            !TryReadString(item.Payload, "branchId", out var branchId) ||
            branchId != step.BranchId ||
            !TryReadInt32(item.Payload, "attemptNo", out var attemptNo) ||
            attemptNo != step.AttemptNo ||
            !TryReadInt32(
                item.Payload,
                "branchOrdinal",
                out var branchOrdinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        if (isEntry)
        {
            var expectedOrdinal = snapshot.Bindings
                .OrderBy(
                    candidate => candidate.TargetUnitId,
                    StringComparer.Ordinal)
                .Select((candidate, index) => new
                {
                    candidate.TargetUnitId,
                    Ordinal = index + 1
                })
                .Single(candidate =>
                    candidate.TargetUnitId == step.TargetUnitId)
                .Ordinal;
            if (item.Operation !=
                    DynamicFlowRuntimeMaterializationOperations
                        .MaterializeEntryAssignment ||
                branchOrdinal != expectedOrdinal ||
                !TryReadString(
                    item.Payload,
                    "commandId",
                    out var launchCommandId) ||
                launchCommandId != instance.LaunchCommandId ||
                !TryReadString(
                    item.Payload,
                    "actorUserId",
                    out var launchActorUserId) ||
                launchActorUserId != instance.IssuerUserId ||
                item.DedupeKey !=
                $"{instance.Id}:{step.FlowStepId}:{step.TargetUnitId}:{step.AttemptNo}:materialize")
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
            }
            return;
        }

        if (item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeSequentialAssignment ||
            branchOrdinal != step.StepOrder ||
            !TryReadString(
                item.Payload,
                "transitionId",
                out var transitionId) ||
            transitionId != step.ActivatedByTransitionId ||
            !TryReadString(item.Payload, "commandId", out var commandId) ||
            item.DedupeKey !=
            $"{instance.Id}:{instance.ExecutionEpoch}:{step.FlowStepId}:{step.BranchId}:{step.AttemptNo}:materialize")
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var receiptId =
            DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(
                instance.Id,
                commandId);
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(candidate => candidate.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        var result = receipt?.ResultSnapshot;
        if (receipt is null ||
            result is null ||
            receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
            receipt.ScopeId != instance.Id ||
            receipt.FlowInstanceId != instance.Id ||
            receipt.WorkId != instance.WorkId ||
            receipt.FlowTemplateVersionId !=
            instance.FlowTemplateVersionId ||
            receipt.CommandType != "FORWARD" ||
            receipt.CommandId != commandId ||
            receipt.Status !=
            DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.UpdatedByUserId is null ||
            !TryReadString(
                item.Payload,
                "actorUserId",
                out var actorUserId) ||
            actorUserId != receipt.UpdatedByUserId ||
            !IsSha256(receipt.RequestHash) ||
            !FixedEquals(
                receipt.CommandIdentityHash,
                Hash(
                    $"{actorUserId}\n{instance.Id}\n{commandId}\nFORWARD")) ||
            !FixedEquals(
                receipt.SnapshotToken,
                Hash(
                    $"{receipt.RequestHash}\n{instance.TopologySnapshotHash}")) ||
            !FixedEquals(
                receipt.ResultSnapshotHash,
                Hash(result.ToJson())) ||
            !TryReadString(
                result,
                "status",
                out var resultStatus) ||
            resultStatus != "ACCEPTED_PENDING_MATERIALIZATION" ||
            !TryReadString(
                result,
                "flowInstanceId",
                out var resultFlowInstanceId) ||
            resultFlowInstanceId != instance.Id ||
            !TryReadInt32(
                result,
                "executionEpoch",
                out var resultExecutionEpoch) ||
            resultExecutionEpoch != instance.ExecutionEpoch ||
            !TryReadString(
                result,
                "nextStepInstanceId",
                out var nextStepInstanceId) ||
            nextStepInstanceId != step.Id ||
            !TryReadString(
                result,
                "nextAssignmentId",
                out var nextAssignmentId) ||
            nextAssignmentId != step.AssignmentId ||
            !TryReadString(
                result,
                "activatedTransitionId",
                out var activatedTransitionId) ||
            activatedTransitionId != step.ActivatedByTransitionId ||
            !TryReadString(
                result,
                "stepInstanceId",
                out var parentStepInstanceId) ||
            !TryReadString(
                result,
                "eventId",
                out var acceptedEventId) ||
            !TryReadInt64(
                item.Payload,
                "materializationEventSequence",
                out var materializationEventSequence))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var parent = await _ctx.DynamicFlowStepInstances
            .Find(candidate =>
                candidate.Id == parentStepInstanceId &&
                candidate.FlowInstanceId == instance.Id &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (parent is null ||
            parent.BranchId != step.BranchId ||
            parent.TargetUnitId != step.TargetUnitId ||
            parent.StepOrder + 1 != step.StepOrder ||
            parent.State != DynamicFlowStepStates.Completed)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var acceptedEvent = await _ctx.DynamicFlowRuntimeEvents
            .Find(candidate => candidate.Id == acceptedEventId)
            .FirstOrDefaultAsync(ct);
        if (acceptedEvent is null ||
            acceptedEvent.Id != StableObjectId(
                $"{instance.Id}\n{instance.ExecutionEpoch}\nforward\n{commandId}\naccepted") ||
            acceptedEvent.FlowInstanceId != instance.Id ||
            acceptedEvent.StepInstanceId != parent.Id ||
            acceptedEvent.ExecutionEpoch != instance.ExecutionEpoch ||
            acceptedEvent.BranchId != parent.BranchId ||
            acceptedEvent.AttemptNo != parent.AttemptNo ||
            acceptedEvent.EventType !=
            DynamicFlowRuntimeEventTypes.SequentialForwardAccepted ||
            acceptedEvent.CommandId != commandId ||
            acceptedEvent.ActorUserId != actorUserId ||
            acceptedEvent.ToState != DynamicFlowStepStates.Completed ||
            acceptedEvent.ReasonCode != "SEQUENTIAL_EDGE_ACTIVATED" ||
            materializationEventSequence != acceptedEvent.Sequence + 1 ||
            !TryReadString(
                acceptedEvent.Payload,
                "fromStepInstanceId",
                out var acceptedParentStepId) ||
            acceptedParentStepId != parent.Id ||
            !TryReadString(
                acceptedEvent.Payload,
                "toStepInstanceId",
                out var acceptedNextStepId) ||
            acceptedNextStepId != step.Id ||
            !TryReadString(
                acceptedEvent.Payload,
                "transitionId",
                out var acceptedTransitionId) ||
            acceptedTransitionId != step.ActivatedByTransitionId ||
            !TryReadString(
                acceptedEvent.Payload,
                "branchId",
                out var acceptedBranchId) ||
            acceptedBranchId != step.BranchId ||
            !TryReadInt32(
                acceptedEvent.Payload,
                "executionEpoch",
                out var acceptedExecutionEpoch) ||
            acceptedExecutionEpoch != step.ExecutionEpoch ||
            !TryReadString(
                acceptedEvent.Payload,
                "definitionRevision",
                out var acceptedDefinitionRevision) ||
            acceptedDefinitionRevision != step.DefinitionRevision ||
            !TryReadString(
                acceptedEvent.Payload,
                "topologyHash",
                out var acceptedTopologyHash) ||
            acceptedTopologyHash != instance.TopologySnapshotHash ||
            !FixedEquals(
                acceptedEvent.PayloadHash,
                Hash(acceptedEvent.Payload.ToJson())))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }
    }

    private async Task RequireFinalizeMaterializationIntentPinsAsync(
        DynamicFlowRuntimeOutboxItem item,
        DynamicFlowStepInstance step,
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        CancellationToken ct)
    {
        var topology = DynamicFlowFinalizeTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var expectedBranchId =
            DynamicFlowFinalizeTopologyContract.BuildBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var expectedStepId =
            DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.EntryNode.NodeId,
                expectedBranchId);
        var expectedAssignmentId =
            DynamicFlowFinalizeTopologyContract.BuildAssignmentId(
                expectedStepId);
        var binding = snapshot.Bindings.SingleOrDefault(candidate =>
            candidate.TargetUnitId == step.TargetUnitId);
        var expectedOrdinal = snapshot.Bindings
            .OrderBy(candidate => candidate.TargetUnitId, StringComparer.Ordinal)
            .Select((candidate, index) => new
            {
                candidate.TargetUnitId,
                Ordinal = index + 1
            })
            .Single(candidate =>
                candidate.TargetUnitId == step.TargetUnitId)
            .Ordinal;

        if (item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeEntryAssignment ||
            step.ExecutionEpoch != instance.ExecutionEpoch ||
            step.FlowStepId != topology.EntryNode.NodeId ||
            step.FormNodeId != topology.EntryForm.FormNodeId ||
            step.FormFamilyId != topology.EntryForm.DynamicFormFamilyId ||
            step.FormVersionId != topology.EntryForm.DynamicFormTemplateId ||
            step.FormVersionNo != topology.EntryForm.DynamicFormVersionNo ||
            !FixedEquals(
                step.FormSchemaHash,
                topology.EntryForm.DynamicFormSchemaHash!) ||
            !FixedEquals(
                step.FormSnapshotHash,
                topology.EntryForm.DynamicFormSnapshotHash!) ||
            step.AttemptNo != 1 ||
            step.BranchId != expectedBranchId ||
            step.Id != expectedStepId ||
            step.AssignmentId != expectedAssignmentId ||
            step.IsCanonicalEpoch == false ||
            step.ParticipantSnapshotId != snapshot.Id ||
            binding is null ||
            !binding.AssigneeUserIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    step.ParticipantUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal)) ||
            !TryReadString(item.Payload, "commandId", out var commandId) ||
            !TryReadString(item.Payload, "actorUserId", out var actorUserId) ||
            actorUserId != instance.IssuerUserId ||
            !TryReadInt32(
                item.Payload,
                "executionEpoch",
                out var executionEpoch) ||
            executionEpoch != step.ExecutionEpoch ||
            !TryReadString(item.Payload, "branchId", out var branchId) ||
            branchId != step.BranchId ||
            !TryReadInt32(item.Payload, "attemptNo", out var attemptNo) ||
            attemptNo != step.AttemptNo ||
            !TryReadInt32(
                item.Payload,
                "branchOrdinal",
                out var branchOrdinal) ||
            branchOrdinal != expectedOrdinal ||
            item.DedupeKey !=
                $"{instance.Id}:{instance.ExecutionEpoch}:{step.FlowStepId}:{step.TargetUnitId}:materialize")
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        if (commandId == instance.LaunchCommandId)
            return;

        var authorizedEpochCommand =
            await _ctx.DynamicFlowRuntimeCommandReceipts.CountDocumentsAsync(
                receipt =>
                    receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                    receipt.ScopeId == instance.Id &&
                    receipt.FlowInstanceId == instance.Id &&
                    receipt.CommandId == commandId &&
                    (receipt.CommandType ==
                        DynamicFlowFinalizeTopologyContract.RollbackCommand ||
                     receipt.CommandType ==
                        DynamicFlowFinalizeTopologyContract.RestartCommand),
                cancellationToken: ct);
        if (authorizedEpochCommand != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }
    }

    private async Task RequireReviewLoopMaterializationIntentPinsAsync(
        DynamicFlowRuntimeOutboxItem item,
        DynamicFlowStepInstance step,
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        bool isEntry,
        CancellationToken ct)
    {
        var topology = DynamicFlowReviewLoopTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var binding = snapshot.Bindings.SingleOrDefault(candidate =>
            candidate.TargetUnitId == step.TargetUnitId);
        var expectedBranchId =
            DynamicFlowReviewLoopTopologyContract.BuildBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var expectedStepId =
            DynamicFlowReviewLoopTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.ReviewNode.NodeId,
                expectedBranchId,
                step.AttemptNo);
        var expectedAssignmentId =
            DynamicFlowReviewLoopTopologyContract.BuildAssignmentId(
                expectedStepId);
        if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                instance.CatalogVersion,
                instance.CatalogSemanticHash) ||
            instance.DefinitionRevision !=
            DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                instance.FlowTemplateVersionId,
                instance.FlowTemplateVersionNo,
                instance.FlowPayloadHash) ||
            instance.TopologySnapshotHash != instance.FlowPayloadHash ||
            step.ExecutionEpoch != instance.ExecutionEpoch ||
            step.FlowStepId != topology.ReviewNode.NodeId ||
            step.FlowStepCode != topology.ReviewNode.NodeCode ||
            step.StepOrder != 1 ||
            step.FormNodeId != topology.ReviewForm.FormNodeId ||
            step.FormVersionId != topology.ReviewForm.DynamicFormTemplateId ||
            step.FormFamilyId != topology.ReviewForm.DynamicFormFamilyId ||
            step.FormVersionNo != topology.ReviewForm.DynamicFormVersionNo ||
            !FixedEquals(
                step.FormSchemaHash,
                topology.ReviewForm.DynamicFormSchemaHash!) ||
            !FixedEquals(
                step.FormSnapshotHash,
                topology.ReviewForm.DynamicFormSnapshotHash!) ||
            step.AttemptNo < 1 ||
            step.ReviewCycleNo != step.AttemptNo ||
            step.BranchId != expectedBranchId ||
            step.Id != expectedStepId ||
            step.AssignmentId != expectedAssignmentId ||
            step.ParticipantSnapshotId != snapshot.Id ||
            step.NextNodeIds.Count != 0 ||
            !step.IsTerminalNode ||
            step.ResultOwnerIdentity !=
            DynamicFlowReviewLoopTopologyContract.BuildOwnerIdentity(
                instance.Id,
                instance.ExecutionEpoch,
                step.FlowStepId,
                step.BranchId,
                step.AttemptNo,
                "result") ||
            step.StatisticOwnerIdentity !=
            DynamicFlowReviewLoopTopologyContract.BuildOwnerIdentity(
                instance.Id,
                instance.ExecutionEpoch,
                step.FlowStepId,
                step.BranchId,
                step.AttemptNo,
                "statistics") ||
            binding is null ||
            !binding.AssigneeUserIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    step.ParticipantUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal)) ||
            !TryReadInt32(
                item.Payload,
                "executionEpoch",
                out var executionEpoch) ||
            executionEpoch != step.ExecutionEpoch ||
            !TryReadString(item.Payload, "branchId", out var branchId) ||
            branchId != step.BranchId ||
            !TryReadInt32(item.Payload, "attemptNo", out var attemptNo) ||
            attemptNo != step.AttemptNo ||
            !TryReadInt32(
                item.Payload,
                "reviewCycleNo",
                out var reviewCycleNo) ||
            reviewCycleNo != step.ReviewCycleNo ||
            !TryReadInt32(
                item.Payload,
                "branchOrdinal",
                out var branchOrdinal) ||
            item.DedupeKey !=
            $"{instance.Id}:{step.FlowStepId}:{step.TargetUnitId}:{step.AttemptNo}:materialize")
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var expectedOrdinal = snapshot.Bindings
            .OrderBy(candidate => candidate.TargetUnitId, StringComparer.Ordinal)
            .Select((candidate, index) => new
            {
                candidate.TargetUnitId,
                Ordinal = index + 1
            })
            .Single(candidate =>
                candidate.TargetUnitId == step.TargetUnitId)
            .Ordinal;
        if (branchOrdinal != expectedOrdinal ||
            !TryReadString(
                item.Payload,
                "actorUserId",
                out var actorUserId) ||
            actorUserId != instance.IssuerUserId)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        if (isEntry)
        {
            if (item.Operation !=
                    DynamicFlowRuntimeMaterializationOperations
                        .MaterializeEntryAssignment ||
                !TryReadString(
                    item.Payload,
                    "commandId",
                    out var launchCommandId) ||
                launchCommandId != instance.LaunchCommandId ||
                step.PreviousAttemptStepInstanceId is not null ||
                step.PreviousAttemptAssignmentId is not null)
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
            }
            return;
        }

        if (item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeReviewAttemptAssignment ||
            step.AttemptNo <= 1 ||
            !TryReadString(
                item.Payload,
                "commandId",
                out var commandId) ||
            !TryReadString(
                item.Payload,
                "previousAttemptStepInstanceId",
                out var previousStepId) ||
            previousStepId != step.PreviousAttemptStepInstanceId ||
            !TryReadString(
                item.Payload,
                "previousAttemptAssignmentId",
                out var previousAssignmentId) ||
            previousAssignmentId != step.PreviousAttemptAssignmentId ||
            !TryReadInt64(
                item.Payload,
                "materializationEventSequence",
                out var materializationSequence))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var previous = await _ctx.DynamicFlowStepInstances
            .Find(candidate =>
                candidate.Id == previousStepId &&
                candidate.FlowInstanceId == instance.Id &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var receiptId =
            DynamicFlowReviewLoopTopologyContract.BuildAdvanceReceiptId(
                instance.Id,
                commandId);
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(candidate => candidate.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        if (previous is null ||
            previous.AttemptNo + 1 != step.AttemptNo ||
            previous.ReviewCycleNo + 1 != step.ReviewCycleNo ||
            previous.AssignmentId != previousAssignmentId ||
            previous.SupersededByStepInstanceId != step.Id ||
            previous.State != DynamicFlowStepStates.Returned ||
            receipt is null ||
            receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
            receipt.ScopeId != instance.Id ||
            receipt.FlowInstanceId != instance.Id ||
            receipt.CommandType !=
            DynamicFlowReviewLoopTopologyContract.ReviewAdvanceCommand ||
            receipt.CommandId != commandId ||
            receipt.Status !=
            DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.ResultSnapshot is null ||
            !FixedEquals(
                receipt.ResultSnapshotHash!,
                Hash(receipt.ResultSnapshot.ToJson())) ||
            !TryReadString(
                receipt.ResultSnapshot,
                "nextStepInstanceId",
                out var receiptStepId) ||
            receiptStepId != step.Id ||
            !TryReadString(
                receipt.ResultSnapshot,
                "nextAssignmentId",
                out var receiptAssignmentId) ||
            receiptAssignmentId != step.AssignmentId ||
            !TryReadInt64(
                receipt.ResultSnapshot,
                "materializationEventSequence",
                out var receiptSequence) ||
            receiptSequence != materializationSequence)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }
    }

    private async Task RequireParallelForkMaterializationIntentPinsAsync(
        DynamicFlowRuntimeOutboxItem item,
        DynamicFlowStepInstance step,
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        bool isEntry,
        CancellationToken ct)
    {
        var topology = RequireParallelFanOutTopology(instance);
        var rootBranchId =
            DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var binding = snapshot.Bindings.SingleOrDefault(candidate =>
            candidate.TargetUnitId == step.TargetUnitId);
        if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                instance.CatalogVersion,
                instance.CatalogSemanticHash) ||
            instance.DefinitionRevision !=
            DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                instance.FlowTemplateVersionId,
                instance.FlowTemplateVersionNo,
                instance.FlowPayloadHash) ||
            instance.TopologySnapshotHash != instance.FlowPayloadHash ||
            step.ExecutionEpoch != instance.ExecutionEpoch ||
            step.AttemptNo != 1 ||
            step.ParticipantSnapshotId != snapshot.Id ||
            binding is null ||
            !binding.AssigneeUserIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    step.ParticipantUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal)) ||
            !TryReadInt32(item.Payload, "executionEpoch", out var epoch) ||
            epoch != step.ExecutionEpoch ||
            !TryReadString(item.Payload, "branchId", out var payloadBranchId) ||
            payloadBranchId != step.BranchId ||
            !TryReadInt32(item.Payload, "attemptNo", out var attemptNo) ||
            attemptNo != step.AttemptNo ||
            !TryReadInt32(item.Payload, "branchOrdinal", out var branchOrdinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        if (isEntry)
        {
            var expectedStepId =
                DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    topology.EntryNode.NodeId,
                    rootBranchId,
                    1);
            var expectedOrdinal = snapshot.Bindings
                .OrderBy(candidate => candidate.TargetUnitId, StringComparer.Ordinal)
                .Select((candidate, index) => new
                {
                    candidate.TargetUnitId,
                    Ordinal = index + 1
                })
                .Single(candidate =>
                    candidate.TargetUnitId == step.TargetUnitId)
                .Ordinal;
            if (step.Id != expectedStepId ||
                step.FlowStepId != topology.EntryNode.NodeId ||
                step.FlowStepCode != topology.EntryNode.NodeCode ||
                step.StepOrder != 1 ||
                step.BranchId != rootBranchId ||
                step.GatewayInstanceId is not null ||
                step.GatewayVersion is not null ||
                step.ContributionId is not null ||
                !step.NextNodeIds.SequenceEqual(
                    new[] { topology.ForkNode.NodeId },
                    StringComparer.Ordinal) ||
                step.IsTerminalNode ||
                item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeEntryAssignment ||
                branchOrdinal != expectedOrdinal ||
                !TryReadString(item.Payload, "commandId", out var launchCommandId) ||
                launchCommandId != instance.LaunchCommandId ||
                item.DedupeKey !=
                $"{instance.Id}:{step.FlowStepId}:{step.TargetUnitId}:1:materialize")
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
            }
            return;
        }

        var branch = topology.Branches.SingleOrDefault(candidate =>
                         candidate.Node.NodeId == step.FlowStepId)
                     ?? throw new InvalidOperationException(
                         "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        var gatewayId =
            DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.ForkNode.NodeId,
                rootBranchId);
        var branchId = DynamicFlowParallelForkTopologyContract.BuildBranchId(
            instance.Id,
            instance.ExecutionEpoch,
            gatewayId,
            branch.Edge.TransitionId,
            step.TargetUnitId);
        var contributionId =
            DynamicFlowParallelForkTopologyContract.BuildContributionId(
                gatewayId,
                branchId,
                branch.Edge.TransitionId);
        var stepId = DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
            instance.Id,
            instance.ExecutionEpoch,
            branch.Node.NodeId,
            branchId,
            1);
        if (item.Operation !=
            DynamicFlowRuntimeMaterializationOperations
                .MaterializeForkBranchAssignment ||
            branchOrdinal != branch.StepOrder ||
            step.Id != stepId ||
            step.FlowStepCode != branch.Node.NodeCode ||
            step.StepOrder != branch.StepOrder ||
            step.FormNodeId != branch.Form.FormNodeId ||
            step.FormFamilyId != branch.Form.DynamicFormFamilyId ||
            step.FormVersionId != branch.Form.DynamicFormTemplateId ||
            step.FormVersionNo != branch.Form.DynamicFormVersionNo ||
            step.FormSchemaHash != branch.Form.DynamicFormSchemaHash ||
            step.FormSnapshotHash != branch.Form.DynamicFormSnapshotHash ||
            step.BranchId != branchId ||
            step.ActivatedByTransitionId != branch.Edge.TransitionId ||
            step.GatewayInstanceId != gatewayId ||
            step.GatewayVersion != 1 ||
            step.ContributionId != contributionId ||
            step.NextNodeIds.Count != 0 ||
            !step.IsTerminalNode ||
            !TryReadString(item.Payload, "transitionId", out var transitionId) ||
            transitionId != branch.Edge.TransitionId ||
            !TryReadString(
                item.Payload,
                "gatewayInstanceId",
                out var payloadGatewayId) ||
            payloadGatewayId != gatewayId ||
            !TryReadInt32(
                item.Payload,
                "gatewayVersion",
                out var gatewayVersion) ||
            gatewayVersion != 1 ||
            !TryReadString(
                item.Payload,
                "contributionId",
                out var payloadContributionId) ||
            payloadContributionId != contributionId ||
            !TryReadString(
                item.Payload,
                "parentBranchId",
                out var parentBranchId) ||
            parentBranchId != rootBranchId ||
            !TryReadString(item.Payload, "commandId", out var commandId) ||
            item.DedupeKey !=
            $"{instance.Id}:{instance.ExecutionEpoch}:{step.FlowStepId}:{branchId}:1:materialize")
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }

        var receiptId =
            DynamicFlowParallelForkTopologyContract.BuildForwardReceiptId(
                instance.Id,
                commandId);
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(candidate => candidate.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        var result = receipt?.ResultSnapshot;
        var resultBranch = result is not null &&
                           result.TryGetValue("activatedBranches", out var branches) &&
                           branches.IsBsonArray
            ? branches.AsBsonArray
                .Select(value => value.AsBsonDocument)
                .SingleOrDefault(value =>
                    value["stepInstanceId"].AsString == step.Id)
            : null;
        var parent = await _ctx.DynamicFlowStepInstances
            .Find(candidate =>
                candidate.FlowInstanceId == instance.Id &&
                candidate.FlowStepId == topology.EntryNode.NodeId &&
                candidate.BranchId == rootBranchId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (receipt is null ||
            result is null ||
            resultBranch is null ||
            receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
            receipt.ScopeId != instance.Id ||
            receipt.CommandType != "FORWARD" ||
            receipt.CommandId != commandId ||
            receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.ResultSnapshotHash != Hash(result.ToJson()) ||
            resultBranch["assignmentId"].AsString != step.AssignmentId ||
            resultBranch["branchId"].AsString != branchId ||
            resultBranch["parentBranchId"].AsString != rootBranchId ||
            resultBranch["gatewayInstanceId"].AsString != gatewayId ||
            resultBranch["contributionId"].AsString != contributionId ||
            resultBranch["transitionId"].AsString != branch.Edge.TransitionId ||
            parent is null ||
            parent.State != DynamicFlowStepStates.Completed)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT");
        }
    }

    private async Task EnsureClaimRunnableAsync(
        DynamicFlowRuntimeOutboxItem item,
        string? runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var leaseHeld = await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
            HeldClaim(item),
            cancellationToken: ct);
        if (leaseHeld != 1)
            throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_LOST");
        var runnableInstance = await _ctx.DynamicFlowInstances.CountDocumentsAsync(
            RuntimeMaterializationFenceFilter(
                item.FlowInstanceId,
                runtimeReconcileLeaseId,
                DateTime.UtcNow),
            cancellationToken: ct);
        if (runnableInstance != 1)
        {
            throw new DynamicFlowRuntimeReconcileFenceException(
                "DYNAMIC_FLOW_RUNTIME_RECONCILE_FENCE_HELD");
        }
    }

    private async Task EnsureClaimRunnableAsync(
        IClientSessionHandle session,
        DynamicFlowRuntimeOutboxItem item,
        string? runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var leaseHeld = await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
            session,
            HeldClaim(item),
            cancellationToken: ct);
        if (leaseHeld != 1)
            throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_LOST");
        var fenced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
            session,
            RuntimeMaterializationFenceFilter(
                item.FlowInstanceId,
                runtimeReconcileLeaseId,
                DateTime.UtcNow),
            Builders<DynamicFlowInstance>.Update.Inc(
                x => x.RuntimeMaterializationFenceRevision,
                1),
            cancellationToken: ct);
        if (fenced.MatchedCount != 1)
        {
            throw new DynamicFlowRuntimeReconcileFenceException(
                "DYNAMIC_FLOW_RUNTIME_RECONCILE_FENCE_HELD");
        }
    }

    private async Task<WorkAssignment> UpsertOwnedAssignmentAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        DynamicFormTemplate form,
        List<UserRef> assignees,
        string assignmentId,
        CancellationToken ct,
        bool allowTerminalRepair = false)
    {
        var existing = await _ctx.WorkAssignments
            .Find(session, x => x.Id == assignmentId)
            .FirstOrDefaultAsync(ct);
        var isFlowFinalNode =
            instance.ArchetypeId is
                DynamicFlowSequentialTopologyContract.ArchetypeId or
                DynamicFlowParallelForkTopologyContract.ArchetypeId or
                DynamicFlowJoinAllTopologyContract.ArchetypeId or
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId or
                DynamicFlowTypedConditionalTopologyContract.ArchetypeId or
                DynamicFlowReviewLoopTopologyContract.ArchetypeId or
                DynamicFlowSubflowTopologyContract.ArchetypeId
                ? step.IsTerminalNode
                : true;
        var parentFlowBranchId =
            IsParallelFanOutArchetype(instance.ArchetypeId) &&
            step.GatewayInstanceId is not null
                ? DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    step.TargetUnitId)
                : null;
        if (existing is null)
        {
            var now = DateTime.UtcNow;
            var entity = new WorkAssignment
            {
                Id = assignmentId,
                WorkId = instance.WorkId,
                DynamicExcelCode = string.Empty,
                DynamicExcelName = string.Empty,
                DynamicFormTemplateId = step.FormVersionId,
                DynamicFormTemplateCode = form.Code,
                DynamicFormTemplateName = form.Name,
                DynamicFormFamilyId = step.FormFamilyId,
                DynamicFormVersionNo = step.FormVersionNo,
                DynamicFormSchemaHash = step.FormSchemaHash,
                WorkType = instance.WorkType,
                AssignmentType = WorkAssignmentTypes.Once,
                AggregationType = WorkAggregationTypes.Matrix,
                Schedule = null,
                Assignees = assignees,
                IsActive = true,
                ParentAssignmentId = null,
                RootAssignmentId = assignmentId,
                Level = 0,
                Code = $"DF-{instance.Id[..8]}-{step.AttemptNo}-{step.TargetUnitId[..6]}",
                Name = string.IsNullOrWhiteSpace(form.Name) ? step.FlowStepCode : form.Name,
                Path = $"/{assignmentId}",
                FlowTemplateId = instance.FlowTemplateId,
                FlowTemplateVersionNo = instance.FlowTemplateVersionNo,
                FlowInstanceId = instance.Id,
                FlowStepId = step.FlowStepId,
                FlowStepCode = step.FlowStepCode,
                FlowStepOrder = step.StepOrder,
                FlowBranchId = step.BranchId,
                ParentFlowBranchId = parentFlowBranchId,
                FlowAttemptNo = step.AttemptNo,
                FlowExecutionEpoch = step.ExecutionEpoch,
                FlowRole = DynamicFlowRuntimePlanner.AssignmentFlowRole,
                FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
                IssuedByUnitId = instance.IssuerUnitId,
                TargetUnitIds = new List<string> { step.TargetUnitId },
                AllowSubFlow =
                    instance.ArchetypeId ==
                    DynamicFlowSubflowTopologyContract.ArchetypeId,
                IsFlowFinalNode = isFlowFinalNode,
                DynamicFlowMaterializationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = instance.IssuerUserId,
                UpdatedByUserId = instance.IssuerUserId,
                IsDeleted = false
            };
            try
            {
                await _ctx.WorkAssignments.InsertOneAsync(
                    session,
                    entity,
                    cancellationToken: ct);
                return entity;
            }
            catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                existing = await _ctx.WorkAssignments
                    .Find(session, x => x.Id == assignmentId)
                    .FirstOrDefaultAsync(ct)
                    ?? throw new InvalidOperationException(
                        "DYNAMIC_FLOW_ASSIGNMENT_RACE_RESULT_MISSING",
                        error);
            }
        }

        if (!string.Equals(existing.FlowInstanceId, instance.Id, StringComparison.Ordinal) ||
            !string.Equals(existing.FlowBranchId, step.BranchId, StringComparison.Ordinal) ||
            !string.Equals(existing.DynamicFormTemplateId, step.FormVersionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_ASSIGNMENT_ID_COLLISION");
        }
        if (!allowTerminalRepair &&
            (existing.CompletedAtUtc.HasValue || !existing.IsActive))
            throw new DynamicFlowRuntimeAssignmentTerminalException(existing.Id);

        var repairedAt = DateTime.UtcNow;
        var assignmentFilter = Builders<WorkAssignment>.Filter.Eq(
                                   x => x.Id,
                                   assignmentId) &
                               Builders<WorkAssignment>.Filter.Eq(
                                   x => x.DynamicFlowMaterializationRevision,
                                   existing.DynamicFlowMaterializationRevision);
        assignmentFilter &=
            Builders<WorkAssignment>.Filter.Eq(
                x => x.FlowInstanceId,
                instance.Id) &
            Builders<WorkAssignment>.Filter.Eq(
                x => x.FlowBranchId,
                step.BranchId) &
            Builders<WorkAssignment>.Filter.Eq(
                x => x.DynamicFormTemplateId,
                step.FormVersionId);
        var repaired = await _ctx.WorkAssignments.UpdateOneAsync(
            session,
            assignmentFilter,
            Builders<WorkAssignment>.Update
                .Set(x => x.WorkId, instance.WorkId)
                .Set(x => x.WorkType, instance.WorkType)
                .Set(x => x.DynamicExcelId, null)
                .Set(x => x.DynamicExcelCode, string.Empty)
                .Set(x => x.DynamicExcelName, string.Empty)
                .Set(x => x.DynamicFormTemplateId, step.FormVersionId)
                .Set(x => x.DynamicFormTemplateCode, form.Code)
                .Set(x => x.DynamicFormTemplateName, form.Name)
                .Set(x => x.DynamicFormFamilyId, step.FormFamilyId)
                .Set(x => x.DynamicFormVersionNo, step.FormVersionNo)
                .Set(x => x.DynamicFormSchemaHash, step.FormSchemaHash)
                .Set(x => x.Assignees, assignees)
                .Set(x => x.LeaderWatchers, new List<UserRef>())
                .Set(x => x.AssignmentType, WorkAssignmentTypes.Once)
                .Set(x => x.AggregationType, WorkAggregationTypes.Matrix)
                .Set(x => x.Schedule, null)
                .Set(x => x.IsActive, true)
                .Set(x => x.IsDeleted, false)
                .Set(x => x.DeletedAtUtc, null)
                .Set(x => x.DeletedByUserId, null)
                .Set(x => x.ParentAssignmentId, null)
                .Set(x => x.RootAssignmentId, assignmentId)
                .Set(x => x.Level, 0)
                .Set(
                    x => x.Code,
                    $"DF-{instance.Id[..8]}-{step.AttemptNo}-{step.TargetUnitId[..6]}")
                .Set(
                    x => x.Name,
                    string.IsNullOrWhiteSpace(form.Name)
                        ? step.FlowStepCode
                        : form.Name)
                .Set(x => x.Path, $"/{assignmentId}")
                .Set(x => x.FlowTemplateId, instance.FlowTemplateId)
                .Set(x => x.FlowTemplateVersionNo, instance.FlowTemplateVersionNo)
                .Set(x => x.FlowInstanceId, instance.Id)
                .Set(x => x.FlowStepId, step.FlowStepId)
                .Set(x => x.FlowStepCode, step.FlowStepCode)
                .Set(x => x.FlowStepOrder, step.StepOrder)
                .Set(x => x.FlowBranchId, step.BranchId)
                .Set(x => x.ParentFlowBranchId, parentFlowBranchId)
                .Set(x => x.FlowAttemptNo, step.AttemptNo)
                .Set(x => x.FlowExecutionEpoch, step.ExecutionEpoch)
                .Set(x => x.FlowRole, DynamicFlowRuntimePlanner.AssignmentFlowRole)
                .Set(x => x.FlowEffectiveStatus, DynamicFlowEffectiveStatuses.Effective)
                .Set(x => x.IssuedByUnitId, instance.IssuerUnitId)
                .Set(x => x.TargetUnitIds, new List<string> { step.TargetUnitId })
                .Set(
                    x => x.AllowSubFlow,
                    instance.ArchetypeId ==
                    DynamicFlowSubflowTopologyContract.ArchetypeId)
                .Set(x => x.IsFlowFinalNode, isFlowFinalNode)
                .Set(x => x.CreatedByUserId, instance.IssuerUserId)
                .Set(x => x.UpdatedAtUtc, repairedAt)
                .Set(x => x.UpdatedByUserId, instance.IssuerUserId)
                .Inc(x => x.DynamicFlowMaterializationRevision, 1),
            cancellationToken: ct);
        if (repaired.ModifiedCount != 1)
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_MATERIALIZATION_FENCE_CONFLICT");
        return await _ctx.WorkAssignments
            .Find(session, x => x.Id == assignmentId && !x.IsDeleted)
            .SingleAsync(ct);
    }

    private async Task<WorkAssignment> FenceOwnedAssignmentAsync(
        IClientSessionHandle session,
        string assignmentId,
        string flowInstanceId,
        CancellationToken ct)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(
                session,
                x =>
                    x.Id == assignmentId &&
                    x.FlowInstanceId == flowInstanceId &&
                    !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_FENCE_SOURCE_MISSING");
        if (assignment.CompletedAtUtc.HasValue || !assignment.IsActive)
            throw new DynamicFlowRuntimeAssignmentTerminalException(assignment.Id);
        var fenced = await _ctx.WorkAssignments.UpdateOneAsync(
            session,
            x =>
                x.Id == assignment.Id &&
                x.FlowInstanceId == flowInstanceId &&
                !x.IsDeleted &&
                x.DynamicFlowMaterializationRevision ==
                assignment.DynamicFlowMaterializationRevision,
            Builders<WorkAssignment>.Update.Inc(
                x => x.DynamicFlowMaterializationRevision,
                1),
            cancellationToken: ct);
        if (fenced.ModifiedCount != 1)
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_MATERIALIZATION_FENCE_CONFLICT");
        assignment.DynamicFlowMaterializationRevision++;
        return assignment;
    }

    private async Task<List<WorkTemplateAssignee>> UpsertBindingsAsync(
        IClientSessionHandle session,
        WorkAssignment assignment,
        DynamicFormTemplate form,
        IReadOnlyList<UserRef> assignees,
        CancellationToken ct)
    {
        var result = new List<WorkTemplateAssignee>();
        foreach (var assignee in assignees.OrderBy(user => user.UserId, StringComparer.Ordinal))
        {
            var id = StableObjectId($"{assignment.Id}\nbinding\n{assignee.UserId}");
            var now = DateTime.UtcNow;
            var filter = Builders<WorkTemplateAssignee>.Filter.Eq(x => x.Id, id);
            var update = Builders<WorkTemplateAssignee>.Update
                .SetOnInsert(x => x.Id, id)
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .SetOnInsert(x => x.CreatedByUserId, assignment.CreatedByUserId)
                .Set(x => x.WorkId, assignment.WorkId)
                .Set(x => x.WorkAssignmentId, assignment.Id)
                .Set(x => x.DynamicFormTemplateId, assignment.DynamicFormTemplateId)
                .Set(x => x.DynamicFormTemplateCode, form.Code)
                .Set(x => x.DynamicFormTemplateName, form.Name)
                .Set(x => x.DynamicFormFamilyId, assignment.DynamicFormFamilyId)
                .Set(x => x.DynamicFormVersionNo, assignment.DynamicFormVersionNo)
                .Set(x => x.DynamicFormSchemaHash, assignment.DynamicFormSchemaHash)
                .Set(x => x.AssigneeUserId, assignee.UserId)
                .Set(x => x.AssigneeUsername, assignee.Username ?? string.Empty)
                .Set(x => x.AssigneeFullName, assignee.FullName ?? string.Empty)
                .Set(x => x.AssigneeUnitId, assignee.UnitId)
                .Set(x => x.AssigneeUnitSymbol, assignee.UnitSymbol)
                .Set(x => x.AssigneeUnitShortName, assignee.UnitShortName)
                .Set(x => x.AssigneeUnitName, assignee.UnitName)
                .Set(x => x.AssignmentType, assignment.AssignmentType)
                .Set(x => x.AggregationType, assignment.AggregationType)
                .Set(x => x.Schedule, assignment.Schedule)
                .Set(x => x.IsActive, true)
                .Set(x => x.IsDeleted, false)
                .Set(x => x.DeletedAtUtc, null)
                .Set(x => x.DeletedByUserId, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, assignment.UpdatedByUserId);
            await _ctx.WorkTemplateAssignees.UpdateOneAsync(
                session,
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                ct);
            result.Add(await _ctx.WorkTemplateAssignees
                .Find(session, filter)
                .SingleAsync(ct));
        }

        var expectedIds = result.Select(binding => binding.Id).ToList();
        await _ctx.WorkTemplateAssignees.UpdateManyAsync(
            session,
            x =>
                x.WorkAssignmentId == assignment.Id &&
                !expectedIds.Contains(x.Id) &&
                x.IsActive &&
                !x.IsDeleted,
            Builders<WorkTemplateAssignee>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, assignment.UpdatedByUserId),
            cancellationToken: ct);
        return result;
    }

    private async Task<WorkReportPeriod> UpsertLaunchPeriodAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        WorkAssignment assignment,
        WorkTemplateAssignee binding,
        CancellationToken ct)
    {
        var id = StableObjectId(
            $"{instance.Id}\nperiod\n{assignment.Id}\n{binding.AssigneeUserId}\n{instance.PeriodKey}");
        var now = DateTime.UtcNow;
        var filter = Builders<WorkReportPeriod>.Filter.Eq(x => x.Id, id);
        var update = Builders<WorkReportPeriod>.Update
            .SetOnInsert(x => x.Id, id)
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, instance.IssuerUserId)
            .Set(x => x.WorkId, instance.WorkId)
            .Set(x => x.WorkAssignmentId, assignment.Id)
            .Set(x => x.WorkTemplateAssigneeId, binding.Id)
            .Set(x => x.DynamicExcelId, assignment.DynamicExcelId)
            .Set(x => x.DynamicExcelCode, assignment.DynamicExcelCode)
            .Set(x => x.DynamicExcelName, assignment.DynamicExcelName)
            .Set(x => x.DynamicFormTemplateId, assignment.DynamicFormTemplateId)
            .Set(x => x.DynamicFormTemplateCode, assignment.DynamicFormTemplateCode)
            .Set(x => x.DynamicFormTemplateName, assignment.DynamicFormTemplateName)
            .Set(x => x.DynamicFormFamilyId, assignment.DynamicFormFamilyId)
            .Set(x => x.DynamicFormVersionNo, assignment.DynamicFormVersionNo)
            .Set(x => x.DynamicFormSchemaHash, assignment.DynamicFormSchemaHash)
            .Set(x => x.AssigneeUserId, binding.AssigneeUserId)
            .Set(x => x.AssigneeUnitId, binding.AssigneeUnitId)
            .Set(x => x.PeriodKey, instance.PeriodKey)
            .Set(x => x.PeriodInstanceKey, instance.PeriodKey)
            .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
            .Set(x => x.ReportTitle, assignment.DynamicFormTemplateName)
            .SetOnInsert(x => x.Status, WorkReportPeriodStatus.Pending)
            .SetOnInsert(x => x.IsOverdue, false)
            .SetOnInsert(x => x.IsHistoricalData, false)
            .Set(x => x.IsActive, true)
            .Set(x => x.IsDeleted, false)
            .Set(x => x.DeletedAtUtc, null)
            .Set(x => x.DeletedByUserId, null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, instance.IssuerUserId);
        await _ctx.WorkReportPeriods.UpdateOneAsync(
            session,
            filter,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
        return await _ctx.WorkReportPeriods
            .Find(session, filter)
            .SingleAsync(ct);
    }

    private async Task<WorkAssignmentQueueItem> UpsertRuntimeQueueAsync(
        IClientSessionHandle session,
        WorkReportPeriod period,
        bool assignmentCompleted,
        string actorUserId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var shouldKeepActive =
            !assignmentCompleted &&
            period.IsActive &&
            !period.IsHistoricalData &&
            WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status);
        var filter = Builders<WorkAssignmentQueueItem>.Filter.And(
            Builders<WorkAssignmentQueueItem>.Filter.Eq(
                x => x.WorkAssignmentId,
                period.WorkAssignmentId),
            Builders<WorkAssignmentQueueItem>.Filter.Eq(
                x => x.AssigneeUserId,
                period.AssigneeUserId),
            Builders<WorkAssignmentQueueItem>.Filter.Eq(
                x => x.PeriodKey,
                period.PeriodKey),
            Builders<WorkAssignmentQueueItem>.Filter.Eq(
                x => x.IsDeleted,
                false));
        var update = Builders<WorkAssignmentQueueItem>.Update
            .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, actorUserId)
            .Set(x => x.WorkId, period.WorkId)
            .Set(x => x.WorkAssignmentId, period.WorkAssignmentId)
            .Set(x => x.AssigneeUserId, period.AssigneeUserId)
            .Set(x => x.PeriodKey, period.PeriodKey)
            .Set(x => x.DueAtUtc, period.DueAtUtc)
            .Set(x => x.NextScanAtUtc, period.DueAtUtc ?? now)
            .Set(x => x.IsActive, shouldKeepActive)
            .Set(x => x.IsDeleted, false)
            .Set(x => x.LastObservedPeriodStatus, (int)period.Status)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);
        return await _ctx.WorkAssignmentQueueItems.FindOneAndUpdateAsync(
            session,
            filter,
            update,
            new FindOneAndUpdateOptions<WorkAssignmentQueueItem>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_QUEUE_UPSERT_FAILED");
    }

    private async Task CompleteOutboxAsync(
        DynamicFlowRuntimeOutboxItem item,
        string? runtimeReconcileLeaseId,
        CancellationToken ct)
    {
        var ordinal = item.Payload["branchOrdinal"].AsInt32;
        var commandId = item.Payload["commandId"].AsString;
        var assignmentId = item.Payload["assignmentId"].AsString;
        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.BeforeOutboxComplete);
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                await EnsureClaimRunnableAsync(
                    session,
                    item,
                    runtimeReconcileLeaseId,
                    transactionCt);
                var now = DateTime.UtcNow;
                _faults.ThrowIfConfigured(
                    commandId,
                    ordinal,
                    DynamicFlowRuntimeFaultPoints.BeforeStepCompleteWrite);
                await CompleteStepRecoveryAwareAsync(
                    session,
                    item.StepInstanceId,
                    assignmentId,
                    now,
                    transactionCt);
                _faults.ThrowIfConfigured(
                    commandId,
                    ordinal,
                    DynamicFlowRuntimeFaultPoints.AfterStepCompleteWrite);
                _faults.ThrowIfConfigured(
                    commandId,
                    ordinal,
                    DynamicFlowRuntimeFaultPoints.BeforeBranchEventWrite);
                await UpsertBranchCompletedEventAsync(
                    session,
                    item,
                    assignmentId,
                    now,
                    transactionCt);
                _faults.ThrowIfConfigured(
                    commandId,
                    ordinal,
                    DynamicFlowRuntimeFaultPoints.AfterBranchEventWrite);
                var completed =
                    await _ctx.DynamicFlowRuntimeOutbox.UpdateOneAsync(
                        session,
                        HeldClaim(item),
                        Builders<DynamicFlowRuntimeOutboxItem>.Update
                            .Set(
                                x => x.Status,
                                DynamicFlowRuntimeOutboxStatuses.Completed)
                            .Set(x => x.CompletedAtUtc, now)
                            .Set(x => x.LeaseId, null)
                            .Set(x => x.LeaseUntilUtc, null)
                            .Set(x => x.LastErrorCode, null)
                            .Set(x => x.UpdatedAtUtc, now),
                        cancellationToken: transactionCt);
                if (completed.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_OUTBOX_LEASE_LOST");
                }
                var workId = await _ctx.WorkAssignments
                    .Find(
                        session,
                        assignment =>
                            assignment.Id == assignmentId &&
                            !assignment.IsDeleted)
                    .Project(assignment => assignment.WorkId)
                    .SingleAsync(transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    workId,
                    transactionCt);
            },
            ct);

        _faults.ThrowIfConfigured(
            commandId,
            ordinal,
            DynamicFlowRuntimeFaultPoints.AfterOutboxComplete);
    }

    private async Task CompleteStepRecoveryAwareAsync(
        IClientSessionHandle session,
        string stepInstanceId,
        string assignmentId,
        DateTime now,
        CancellationToken ct)
    {
        var step = await _ctx.DynamicFlowStepInstances
            .Find(session, x => x.Id == stepInstanceId)
            .SingleAsync(ct);
        if (step.State == DynamicFlowStepStates.Retrying)
        {
            var reconciled = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                session,
                x =>
                    x.Id == stepInstanceId &&
                    x.State == DynamicFlowStepStates.Retrying &&
                    x.Revision == step.Revision,
                Builders<DynamicFlowStepInstance>.Update
                    .Set(x => x.State, DynamicFlowStepStates.Reconciled)
                    .Set(x => x.AssignmentId, assignmentId)
                    .Set(x => x.LastErrorCode, null)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Inc(x => x.Revision, 1),
                cancellationToken: ct);
            if (reconciled.ModifiedCount != 1)
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
            step.State = DynamicFlowStepStates.Reconciled;
            step.Revision++;
        }

        var expected = step.State;
        var next = expected == DynamicFlowStepStates.Reconciled
            ? ResolveStepResumeState(step)
            : DynamicFlowStepStates.Assigned;
        if (expected is DynamicFlowStepStates.Materializing or DynamicFlowStepStates.Reconciled)
        {
            if (expected == DynamicFlowStepStates.Reconciled)
                DynamicFlowRuntimeStateContract.RequireStepTransition(expected, next);
            var restored = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                session,
                x =>
                    x.Id == stepInstanceId &&
                    x.State == expected &&
                    x.Revision == step.Revision,
                Builders<DynamicFlowStepInstance>.Update
                    .Set(x => x.State, next)
                    .Set(x => x.AssignmentId, assignmentId)
                    .Set(x => x.ResumeState, null)
                    .Set(x => x.LastErrorCode, null)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Inc(x => x.Revision, 1),
                cancellationToken: ct);
            if (restored.ModifiedCount != 1)
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
        }
    }

    private static string ResolveStepResumeState(DynamicFlowStepInstance step)
        => step.ResumeState is
            DynamicFlowStepStates.Assigned or
            DynamicFlowStepStates.InProgress or
            DynamicFlowStepStates.Submitted or
            DynamicFlowStepStates.Returned or
            DynamicFlowStepStates.Approved
                ? step.ResumeState
                : DynamicFlowStepStates.Assigned;

    private async Task MarkOutboxFailureAsync(
        DynamicFlowRuntimeOutboxItem item,
        Exception error,
        CancellationToken ct,
        string? runtimeReconcileLeaseId)
    {
        var now = DateTime.UtcNow;
        var nextAttempt = item.AttemptCount + 1;
        var errorCode = StableErrorCode(error);
        var actorUserId = item.Payload.TryGetValue("actorUserId", out var actor)
            ? actor.AsString
            : null;
        var terminalInstance = await _ctx.DynamicFlowInstances.CountDocumentsAsync(
            x =>
                x.Id == item.FlowInstanceId &&
                (x.State == DynamicFlowInstanceStates.Failed ||
                 x.State == DynamicFlowInstanceStates.Terminated),
            cancellationToken: ct);
        var deadLetter = nextAttempt >= MaxAttempts || terminalInstance != 0;
        if (deadLetter)
        {
            await TerminalizeFailureAsync(
                item,
                nextAttempt,
                errorCode,
                actorUserId,
                now,
                ct,
                runtimeReconcileLeaseId);
            return;
        }

        await RecordRecoverableFailureAsync(
            item,
            nextAttempt,
            errorCode,
            actorUserId,
            now,
            ct,
            runtimeReconcileLeaseId);
    }

    private async Task ReleaseClaimForRuntimeReconcileAsync(
        DynamicFlowRuntimeOutboxItem item,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.DynamicFlowRuntimeOutbox.UpdateOneAsync(
            HeldClaim(item, requireUnexpiredLease: false),
            Builders<DynamicFlowRuntimeOutboxItem>.Update
                .Set(x => x.Status, DynamicFlowRuntimeOutboxStatuses.Pending)
                .Set(x => x.NextAttemptAtUtc, now)
                .Set(x => x.LeaseId, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);
    }

    private Task RecordRecoverableFailureAsync(
        DynamicFlowRuntimeOutboxItem item,
        int nextAttempt,
        string errorCode,
        string? actorUserId,
        DateTime now,
        CancellationToken ct,
        string? runtimeReconcileLeaseId)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var fenceNow = DateTime.UtcNow;
                var runtimeInstance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeMaterializationFenceFilter(
                            item.FlowInstanceId,
                            runtimeReconcileLeaseId,
                            fenceNow))
                    .FirstOrDefaultAsync(transactionCt);
                if (runtimeInstance is null)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_FAILURE_FENCE_CONFLICT");
                }
                var failed = await _ctx.DynamicFlowRuntimeOutbox.UpdateOneAsync(
                    session,
                    HeldClaim(item),
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(x => x.Status, DynamicFlowRuntimeOutboxStatuses.Failed)
                        .Set(x => x.AttemptCount, nextAttempt)
                        .Set(
                            x => x.NextAttemptAtUtc,
                            now.AddSeconds(Math.Min(2 << Math.Min(nextAttempt, 8), 300)))
                        .Set(x => x.LeaseId, null)
                        .Set(x => x.LeaseUntilUtc, null)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                if (failed.ModifiedCount != 1)
                    return;

                var runtimeStep = await _ctx.DynamicFlowStepInstances
                    .Find(session, x => x.Id == item.StepInstanceId && !x.IsDeleted)
                    .SingleAsync(transactionCt);
                var resumeState = runtimeStep.State == DynamicFlowStepStates.Materializing
                    ? DynamicFlowStepStates.Assigned
                    : ResolveStepResumeState(runtimeStep);
                if (runtimeStep.State == DynamicFlowStepStates.Reconciled)
                {
                    DynamicFlowRuntimeStateContract.RequireStepTransition(
                        runtimeStep.State,
                        resumeState);
                    var restored = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                        session,
                        DynamicFlowRuntimeRevisionContract.StepCas(
                            runtimeStep.Id,
                            runtimeStep.Revision,
                            runtimeStep.State),
                        Builders<DynamicFlowStepInstance>.Update
                            .Set(x => x.State, resumeState)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1),
                        cancellationToken: transactionCt);
                    if (restored.ModifiedCount != 1)
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
                    runtimeStep.State = resumeState;
                    runtimeStep.Revision++;
                }
                UpdateDefinition<DynamicFlowStepInstance> stepFailureUpdate;
                if (runtimeStep.State is DynamicFlowStepStates.Materializing
                    or DynamicFlowStepStates.Assigned
                    or DynamicFlowStepStates.InProgress
                    or DynamicFlowStepStates.Submitted
                    or DynamicFlowStepStates.Returned
                    or DynamicFlowStepStates.Approved)
                {
                    DynamicFlowRuntimeStateContract.RequireStepTransition(
                        runtimeStep.State,
                        DynamicFlowStepStates.Partial);
                    stepFailureUpdate = Builders<DynamicFlowStepInstance>.Update
                        .Set(x => x.State, DynamicFlowStepStates.Partial)
                        .Set(x => x.ResumeState, resumeState)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Inc(x => x.Revision, 1);
                }
                else if (runtimeStep.State is DynamicFlowStepStates.Partial
                         or DynamicFlowStepStates.Retrying)
                {
                    stepFailureUpdate = Builders<DynamicFlowStepInstance>.Update
                        .Set(x => x.ResumeState, resumeState)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId);
                }
                else
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");
                }
                var stepPartial = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                    session,
                    DynamicFlowRuntimeRevisionContract.StepCas(
                        runtimeStep.Id,
                        runtimeStep.Revision,
                        runtimeStep.State),
                    stepFailureUpdate,
                    cancellationToken: transactionCt);
                if (stepPartial.ModifiedCount != 1)
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_RETRY_STEP_STATE_CONFLICT");

                if (runtimeInstance.State == DynamicFlowInstanceStates.Reconciled)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        runtimeInstance.State,
                        DynamicFlowInstanceStates.Active);
                    var restored = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            runtimeInstance.Id,
                            runtimeReconcileLeaseId,
                            DateTime.UtcNow) &
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            runtimeInstance.Id,
                            runtimeInstance.Revision,
                            runtimeInstance.State),
                        Builders<DynamicFlowInstance>.Update
                            .Set(x => x.State, DynamicFlowInstanceStates.Active)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1),
                        cancellationToken: transactionCt);
                    if (restored.ModifiedCount != 1)
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_RUNTIME_RETRY_INSTANCE_STATE_CONFLICT");
                    runtimeInstance.State = DynamicFlowInstanceStates.Active;
                    runtimeInstance.Revision++;
                }
                UpdateDefinition<DynamicFlowInstance> instanceFailureUpdate;
                if (runtimeInstance.State is DynamicFlowInstanceStates.Materializing
                    or DynamicFlowInstanceStates.Active)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        runtimeInstance.State,
                        DynamicFlowInstanceStates.Partial);
                    instanceFailureUpdate = Builders<DynamicFlowInstance>.Update
                        .Set(x => x.State, DynamicFlowInstanceStates.Partial)
                        .Set(x => x.ResumeState, DynamicFlowInstanceStates.Active)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Inc(x => x.Revision, 1);
                }
                else if (runtimeInstance.State is DynamicFlowInstanceStates.Partial
                         or DynamicFlowInstanceStates.Retrying)
                {
                    instanceFailureUpdate = Builders<DynamicFlowInstance>.Update
                        .Set(x => x.ResumeState, DynamicFlowInstanceStates.Active)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId);
                }
                else
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_RETRY_INSTANCE_STATE_CONFLICT");
                }
                var instancePartial = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    RuntimeMaterializationFenceFilter(
                        runtimeInstance.Id,
                        runtimeReconcileLeaseId,
                        DateTime.UtcNow) &
                    DynamicFlowRuntimeRevisionContract.InstanceCas(
                        runtimeInstance.Id,
                        runtimeInstance.Revision,
                        runtimeInstance.State),
                    instanceFailureUpdate,
                    cancellationToken: transactionCt);
                if (instancePartial.ModifiedCount != 1)
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_RETRY_INSTANCE_STATE_CONFLICT");
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    runtimeInstance.WorkId,
                    transactionCt);
            },
            ct);

    private Task TerminalizeFailureAsync(
        DynamicFlowRuntimeOutboxItem item,
        int nextAttempt,
        string errorCode,
        string? actorUserId,
        DateTime now,
        CancellationToken ct,
        string? runtimeReconcileLeaseId)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var instance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeMaterializationFenceFilter(
                            item.FlowInstanceId,
                            runtimeReconcileLeaseId,
                            DateTime.UtcNow))
                    .FirstOrDefaultAsync(transactionCt);
                if (instance is null)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_TERMINALIZATION_FENCE_CONFLICT");
                }
                var terminalizable =
                    instance.State is DynamicFlowInstanceStates.Pending
                        or DynamicFlowInstanceStates.Materializing
                        or DynamicFlowInstanceStates.Partial
                        or DynamicFlowInstanceStates.Retrying
                        or DynamicFlowInstanceStates.Reconciled
                        or DynamicFlowInstanceStates.Failed
                        or DynamicFlowInstanceStates.Terminated;
                if (!terminalizable)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_RUNTIME_TERMINALIZATION_STATE_CONFLICT");
                }

                var failed = await _ctx.DynamicFlowRuntimeOutbox.UpdateOneAsync(
                    session,
                    HeldClaim(item),
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(x => x.Status, DynamicFlowRuntimeOutboxStatuses.DeadLetter)
                        .Set(x => x.AttemptCount, nextAttempt)
                        .Set(x => x.NextAttemptAtUtc, DateTime.MaxValue)
                        .Set(x => x.LeaseId, null)
                        .Set(x => x.LeaseUntilUtc, null)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                if (failed.ModifiedCount != 1)
                    return;

                await _ctx.DynamicFlowRuntimeOutbox.UpdateManyAsync(
                    session,
                    x =>
                        x.FlowInstanceId == item.FlowInstanceId &&
                        (x.Status == DynamicFlowRuntimeOutboxStatuses.Pending ||
                         x.Status == DynamicFlowRuntimeOutboxStatuses.Failed),
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(x => x.Status, DynamicFlowRuntimeOutboxStatuses.DeadLetter)
                        .Set(x => x.NextAttemptAtUtc, DateTime.MaxValue)
                        .Set(x => x.LeaseId, null)
                        .Set(x => x.LeaseUntilUtc, null)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                await _ctx.DynamicFlowStepInstances.UpdateManyAsync(
                    session,
                    x =>
                        x.FlowInstanceId == item.FlowInstanceId &&
                        (x.State == DynamicFlowStepStates.Pending ||
                         x.State == DynamicFlowStepStates.Materializing ||
                         x.State == DynamicFlowStepStates.Assigned ||
                         x.State == DynamicFlowStepStates.Partial ||
                         x.State == DynamicFlowStepStates.Retrying ||
                         x.State == DynamicFlowStepStates.Reconciled),
                    Builders<DynamicFlowStepInstance>.Update
                        .Set(x => x.State, DynamicFlowStepStates.Failed)
                        .Set(x => x.LastErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Inc(x => x.Revision, 1),
                    cancellationToken: transactionCt);
                if (instance.State is DynamicFlowInstanceStates.Pending
                    or DynamicFlowInstanceStates.Materializing
                    or DynamicFlowInstanceStates.Partial
                    or DynamicFlowInstanceStates.Retrying
                    or DynamicFlowInstanceStates.Reconciled)
                {
                    var instanceFailed = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            item.FlowInstanceId,
                            runtimeReconcileLeaseId,
                            DateTime.UtcNow) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.Revision,
                            instance.Revision) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.State,
                            instance.State),
                        Builders<DynamicFlowInstance>.Update
                            .Set(x => x.State, DynamicFlowInstanceStates.Failed)
                            .Set(x => x.LastErrorCode, errorCode)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1),
                        cancellationToken: transactionCt);
                    if (instanceFailed.ModifiedCount != 1)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_RUNTIME_TERMINALIZATION_STATE_CONFLICT");
                    }
                }

                var terminalSteps = await _ctx.DynamicFlowStepInstances
                    .Find(
                        session,
                        x => x.FlowInstanceId == item.FlowInstanceId && !x.IsDeleted)
                    .SortBy(x => x.TargetUnitId)
                    .ToListAsync(transactionCt);
                var terminalResult = new BsonDocument
                {
                    { "flowInstanceId", item.FlowInstanceId },
                    { "status", "FAILED" },
                    { "instanceState", DynamicFlowInstanceStates.Failed },
                    { "errorCode", errorCode },
                    { "stepInstanceIds", new BsonArray(terminalSteps.Select(step => step.Id)) },
                    {
                        "assignmentIds",
                        new BsonArray(terminalSteps
                            .Where(step => !string.IsNullOrWhiteSpace(step.AssignmentId))
                            .Select(step => step.AssignmentId!))
                    }
                };
                await _ctx.DynamicFlowRuntimeCommandReceipts.UpdateOneAsync(
                    session,
                    x =>
                        x.FlowInstanceId == item.FlowInstanceId &&
                        x.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                        x.CommandType == "LAUNCH" &&
                        x.Status == DynamicFlowRuntimeCommandStatuses.Pending,
                    Builders<DynamicFlowRuntimeCommandReceipt>.Update
                        .Set(x => x.Status, DynamicFlowRuntimeCommandStatuses.Failed)
                        .Set(x => x.ResultSnapshot, terminalResult)
                        .Set(x => x.ResultSnapshotHash, Hash(terminalResult.ToJson()))
                        .Set(x => x.ErrorCode, errorCode)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Set(x => x.CompletedAtUtc, now),
                    cancellationToken: transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    instance.WorkId,
                    transactionCt);
            },
            ct);

    private async Task EnsureRetryingAsync(
        string instanceId,
        string actorUserId,
        CancellationToken ct,
        string? runtimeReconcileLeaseId = null)
    {
        var commandId = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == instanceId && !x.IsDeleted)
            .Project(x => x.LaunchCommandId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeRetryStateWrite);
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var instance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeMaterializationFenceFilter(
                            instanceId,
                            runtimeReconcileLeaseId,
                            now))
                    .FirstOrDefaultAsync(transactionCt);
                if (instance is null)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RETRY_FENCE_CONFLICT");
                }

                if (instance.State == DynamicFlowInstanceStates.Partial)
                {
                    await _ctx.DynamicFlowStepInstances.UpdateManyAsync(
                        session,
                        x =>
                            x.FlowInstanceId == instanceId &&
                            x.State == DynamicFlowStepStates.Partial,
                        Builders<DynamicFlowStepInstance>.Update
                            .Set(x => x.State, DynamicFlowStepStates.Retrying)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, 1),
                        cancellationToken: transactionCt);
                }

                var update =
                    Builders<DynamicFlowInstance>.Update.Inc(
                        x => x.RuntimeMaterializationFenceRevision,
                        1);
                if (instance.State == DynamicFlowInstanceStates.Partial)
                {
                    update = update
                        .Set(x => x.State, DynamicFlowInstanceStates.Retrying)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, actorUserId)
                        .Inc(x => x.Revision, 1);
                }
                var fenced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    RuntimeMaterializationFenceFilter(
                        instanceId,
                        runtimeReconcileLeaseId,
                        now) &
                    Builders<DynamicFlowInstance>.Filter.Eq(
                        x => x.Revision,
                        instance.Revision),
                    update,
                    cancellationToken: transactionCt);
                if (fenced.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RETRY_FENCE_CONFLICT");
                }
                if (instance.State == DynamicFlowInstanceStates.Partial)
                {
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        instance.WorkId,
                        transactionCt);
                }
            },
            ct);
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterRetryStateWrite);
    }

    private async Task TryFinalizeInstanceAsync(
        string instanceId,
        CancellationToken ct,
        string? runtimeReconcileLeaseId = null)
    {
        var incomplete = await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
            x =>
                x.FlowInstanceId == instanceId &&
                x.Status != DynamicFlowRuntimeOutboxStatuses.Completed &&
                x.Status != DynamicFlowRuntimeOutboxStatuses.DeadLetter,
            cancellationToken: ct);
        if (incomplete != 0)
            return;

        var instance = await _ctx.DynamicFlowInstances
            .Find(RuntimeMaterializationFenceFilter(
                instanceId,
                runtimeReconcileLeaseId,
                DateTime.UtcNow))
            .FirstOrDefaultAsync(ct);
        if (instance is null)
            return;
        if (instance.State == DynamicFlowInstanceStates.Active)
        {
            await SyncParentChildActiveStateAsync(instance, ct);
        }
        if (instance.ArchetypeId is
                DynamicFlowSequentialTopologyContract.ArchetypeId or
                DynamicFlowParallelForkTopologyContract.ArchetypeId or
                DynamicFlowJoinAllTopologyContract.ArchetypeId or
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId or
                DynamicFlowTypedConditionalTopologyContract.ArchetypeId or
                DynamicFlowReviewLoopTopologyContract.ArchetypeId or
                DynamicFlowSubflowTopologyContract.ArchetypeId or
                DynamicFlowFinalizeTopologyContract.ArchetypeId &&
            instance.State is DynamicFlowInstanceStates.Active or
                DynamicFlowInstanceStates.Completed)
        {
            // P6-01 keeps the aggregate ACTIVE between sequential nodes. The
            // state projector owns terminal completion after the final pinned
            // node; materialization must not reinterpret an intermediate chain.
            return;
        }
        var ledger = await InspectReconcileAsync(instanceId, false, instance.State, ct);
        // ACTIVE is written by this finalizer, so requiring the full state ledger here
        // would create a circular gate. The materialization ledger is the finalization
        // oracle; the transaction below rechecks both it and the durable reconcile fence.
        if (!ledger.ExactMaterializationLedgerConverged)
            return;

        _faults.ThrowIfConfigured(
            instance.LaunchCommandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeInstanceFinalizeWrite);
        _faults.ThrowIfConfigured(
            instance.LaunchCommandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeReceiptFinalizeWrite);
        var finalized = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var now = DateTime.UtcNow;
                var current = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeMaterializationFenceFilter(
                            instanceId,
                            runtimeReconcileLeaseId,
                            now))
                    .FirstOrDefaultAsync(transactionCt);
                if (current is null)
                    return false;
                var sourceMembershipChanged = false;
                var remaining = await _ctx.DynamicFlowRuntimeOutbox.CountDocumentsAsync(
                    session,
                    x =>
                        x.FlowInstanceId == instanceId &&
                        x.Status != DynamicFlowRuntimeOutboxStatuses.Completed &&
                        x.Status != DynamicFlowRuntimeOutboxStatuses.DeadLetter,
                    cancellationToken: transactionCt);
                if (remaining != 0)
                    return false;

                var hasRecoveryReconciledEvent =
                    await _ctx.DynamicFlowRuntimeEvents.CountDocumentsAsync(
                        session,
                        x =>
                            x.Id == RecoveryEventId(
                                instanceId,
                                DynamicFlowRuntimeEventTypes.RecoveryReconciled,
                                current.RuntimeRecoveryEpoch),
                        cancellationToken: transactionCt) > 0;

                if (current.State == DynamicFlowInstanceStates.Retrying)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        current.State,
                        DynamicFlowInstanceStates.Reconciled);
                    var reconciled = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            instanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.State,
                            DynamicFlowInstanceStates.Retrying) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.Revision,
                            current.Revision),
                        Builders<DynamicFlowInstance>.Update
                            .Set(
                                x => x.State,
                                DynamicFlowInstanceStates.Reconciled)
                            .Set(x => x.LastErrorCode, null)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Inc(x => x.Revision, 1)
                            .Inc(x => x.NextEventSequence, 1),
                        cancellationToken: transactionCt);
                    if (reconciled.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_FINALIZE_FENCE_CONFLICT");
                    }
                    await UpsertRecoveryEventAsync(
                        session,
                        current,
                        DynamicFlowRuntimeEventTypes.RecoveryReconciled,
                        current.IssuerUserId,
                        current.NextEventSequence,
                        DynamicFlowInstanceStates.Reconciled,
                        now,
                        transactionCt);
                    current.State = DynamicFlowInstanceStates.Reconciled;
                    current.Revision++;
                    current.NextEventSequence++;
                    hasRecoveryReconciledEvent = true;
                    sourceMembershipChanged = true;
                }
                else if (current.State == DynamicFlowInstanceStates.Reconciled &&
                         !hasRecoveryReconciledEvent)
                {
                    var sequenceReserved =
                        await _ctx.DynamicFlowInstances.UpdateOneAsync(
                            session,
                            RuntimeMaterializationFenceFilter(
                                instanceId,
                                runtimeReconcileLeaseId,
                                now) &
                            Builders<DynamicFlowInstance>.Filter.Eq(
                                x => x.State,
                                DynamicFlowInstanceStates.Reconciled) &
                            Builders<DynamicFlowInstance>.Filter.Eq(
                                x => x.Revision,
                                current.Revision),
                            Builders<DynamicFlowInstance>.Update
                                .Inc(x => x.NextEventSequence, 1),
                            cancellationToken: transactionCt);
                    if (sequenceReserved.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_FINALIZE_FENCE_CONFLICT");
                    }
                    await UpsertRecoveryEventAsync(
                        session,
                        current,
                        DynamicFlowRuntimeEventTypes.RecoveryReconciled,
                        current.IssuerUserId,
                        current.NextEventSequence,
                        DynamicFlowInstanceStates.Reconciled,
                        now,
                        transactionCt);
                    current.NextEventSequence++;
                }

                if (current.State is DynamicFlowInstanceStates.Materializing
                    or DynamicFlowInstanceStates.Reconciled)
                {
                    DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                        current.State,
                        DynamicFlowInstanceStates.Active);
                    var activated = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        RuntimeMaterializationFenceFilter(
                            instanceId,
                            runtimeReconcileLeaseId,
                            now) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.State,
                            current.State) &
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.Revision,
                            current.Revision),
                        Builders<DynamicFlowInstance>.Update
                            .Set(x => x.State, DynamicFlowInstanceStates.Active)
                            .Set(x => x.ResumeState, null)
                            .Set(x => x.LastErrorCode, null)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Inc(x => x.Revision, 1),
                        cancellationToken: transactionCt);
                    if (activated.ModifiedCount != 1)
                    {
                        throw new DynamicFlowRuntimeReconcileFenceException(
                            "DYNAMIC_FLOW_RUNTIME_FINALIZE_FENCE_CONFLICT");
                    }
                    current.State = DynamicFlowInstanceStates.Active;
                    current.Revision++;
                    sourceMembershipChanged = true;
                }
                if (current.State != DynamicFlowInstanceStates.Active)
                    return false;

                _faults.ThrowIfConfigured(
                    instance.LaunchCommandId,
                    0,
                    DynamicFlowRuntimeFaultPoints.AfterInstanceFinalizeWrite);
                var steps = await _ctx.DynamicFlowStepInstances
                    .Find(
                        session,
                        x =>
                            x.FlowInstanceId == instanceId &&
                            !x.IsDeleted)
                    .SortBy(x => x.TargetUnitId)
                    .ToListAsync(transactionCt);
                var result = new BsonDocument
                {
                    { "flowInstanceId", instanceId },
                    { "status", "SUCCEEDED" },
                    { "instanceState", DynamicFlowInstanceStates.Active },
                    { "stepInstanceIds", new BsonArray(steps.Select(step => step.Id)) },
                    {
                        "assignmentIds",
                        new BsonArray(steps.Select(step => step.AssignmentId ?? string.Empty))
                    }
                };
                await _ctx.DynamicFlowRuntimeCommandReceipts.UpdateOneAsync(
                    session,
                    x =>
                        x.FlowInstanceId == instanceId &&
                        x.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                        x.CommandType == "LAUNCH" &&
                        x.Status == DynamicFlowRuntimeCommandStatuses.Pending,
                    Builders<DynamicFlowRuntimeCommandReceipt>.Update
                        .Set(
                            x => x.Status,
                            DynamicFlowRuntimeCommandStatuses.Succeeded)
                        .Set(x => x.ResultSnapshot, result)
                        .Set(x => x.ResultSnapshotHash, Hash(result.ToJson()))
                        .Set(x => x.ErrorCode, null)
                        .Set(x => x.CompletedAtUtc, now),
                    cancellationToken: transactionCt);
                _faults.ThrowIfConfigured(
                    instance.LaunchCommandId,
                    0,
                    DynamicFlowRuntimeFaultPoints.AfterReceiptFinalizeWrite);
                if (sourceMembershipChanged)
                {
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        current.WorkId,
                        transactionCt);
                }
                return true;
            },
            ct);
        if (!finalized)
            return;
        var finalizedInstance = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == instanceId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (finalizedInstance?.State ==
            DynamicFlowInstanceStates.Active)
        {
            await SyncParentChildActiveStateAsync(
                finalizedInstance,
                ct);
        }
    }

    private async Task SyncParentChildActiveStateAsync(
        DynamicFlowInstance child,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(child.ParentInstanceId) ||
            string.IsNullOrWhiteSpace(child.ParentStepInstanceId))
        {
            return;
        }
        await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
            candidate =>
                candidate.Id == child.ParentStepInstanceId &&
                candidate.FlowInstanceId == child.ParentInstanceId &&
                candidate.ChildInstanceId == child.Id &&
                candidate.ChildFlowTemplateId ==
                    child.FlowTemplateId &&
                candidate.ChildFlowVersionId ==
                    child.FlowTemplateVersionId &&
                candidate.State ==
                    DynamicFlowStepStates.WaitingChild &&
                candidate.ChildState ==
                    DynamicFlowInstanceStates.Materializing &&
                !candidate.IsDeleted,
            Builders<DynamicFlowStepInstance>.Update
                .Set(
                    candidate => candidate.ChildState,
                    DynamicFlowInstanceStates.Active)
                .Set(
                    candidate => candidate.UpdatedAtUtc,
                    DateTime.UtcNow)
                .Set(
                    candidate => candidate.UpdatedByUserId,
                    child.IssuerUserId),
            cancellationToken: ct);
    }

    private async Task<DynamicFlowConfirmResponse> BuildResponseAsync(
        string receiptId,
        string snapshotToken,
        CancellationToken ct)
    {
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.Id == receiptId)
            .SingleAsync(ct);
        if (receipt.Status is DynamicFlowRuntimeCommandStatuses.Succeeded
            or DynamicFlowRuntimeCommandStatuses.Failed)
        {
            var stored = receipt.ResultSnapshot
                         ?? throw new InvalidOperationException(
                             "DYNAMIC_FLOW_RUNTIME_RESULT_SNAPSHOT_MISSING");
            if (!string.Equals(
                    receipt.ResultSnapshotHash,
                    Hash(stored.ToJson()),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_RUNTIME_RESULT_SNAPSHOT_TAMPERED");
            }

            return new DynamicFlowConfirmResponse
            {
                CommandId = receipt.CommandId,
                RequestHash = receipt.RequestHash,
                SnapshotToken = snapshotToken,
                Status = stored["status"].AsString,
                BusinessWritePerformed = true,
                FlowInstanceId = stored["flowInstanceId"].AsString,
                InstanceState = stored["instanceState"].AsString,
                StepInstanceIds = stored["stepInstanceIds"].AsBsonArray
                    .Select(value => value.AsString)
                    .ToList(),
                AssignmentIds = stored["assignmentIds"].AsBsonArray
                    .Select(value => value.AsString)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList()
            };
        }

        var instance = receipt.FlowInstanceId is null
            ? null
            : await _ctx.DynamicFlowInstances
                .Find(x => x.Id == receipt.FlowInstanceId)
                .FirstOrDefaultAsync(ct);
        var steps = receipt.FlowInstanceId is null
            ? new List<DynamicFlowStepInstance>()
            : await _ctx.DynamicFlowStepInstances
                .Find(x => x.FlowInstanceId == receipt.FlowInstanceId && !x.IsDeleted)
                .SortBy(x => x.TargetUnitId)
                .ToListAsync(ct);
        return new DynamicFlowConfirmResponse
        {
            CommandId = receipt.CommandId,
            RequestHash = receipt.RequestHash,
            SnapshotToken = snapshotToken,
            Status = instance?.State == DynamicFlowInstanceStates.Partial
                        ? "RECOVERY_REQUIRED"
                        : "MATERIALIZING",
            BusinessWritePerformed = true,
            FlowInstanceId = receipt.FlowInstanceId,
            InstanceState = instance?.State,
            StepInstanceIds = steps.Select(step => step.Id).ToList(),
            AssignmentIds = steps
                .Where(step => !string.IsNullOrWhiteSpace(step.AssignmentId))
                .Select(step => step.AssignmentId!)
                .ToList()
        };
    }

    private async Task<DynamicFlowRuntimeReconcileResult> InspectReconcileAsync(
        string instanceId,
        bool apply,
        string stateBefore,
        CancellationToken ct)
    {
        var instance = await _ctx.DynamicFlowInstances.Find(x => x.Id == instanceId).SingleAsync(ct);
        var steps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == instanceId &&
                x.ExecutionEpoch == instance.ExecutionEpoch &&
                x.IsCanonicalEpoch != false &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var participantSnapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct);
        var immutableAuthorityProven =
            participantSnapshot is not null &&
            await HasExactRuntimeImmutableAuthorityAsync(
                instance,
                participantSnapshot,
                steps,
                ct);
        var ownershipProof = new List<RuntimeBranchOwnership>();
        var ownershipProven =
            immutableAuthorityProven &&
            FixedEquals(
                participantSnapshot!.SnapshotHash,
                instance.ParticipantSnapshotHash) &&
            TryBuildOwnershipProof(
                instance,
                participantSnapshot,
                steps,
                out ownershipProof);
        var assignmentIds = ownershipProof
            .Select(branch => branch.AssignmentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var formVersionIds = ownershipProof
            .Select(branch => branch.Step.FormVersionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var runtimeForms = formVersionIds.Count == 0
            ? new List<DynamicFormTemplate>()
            : await _ctx.DynamicFormTemplates
                .Find(x =>
                    formVersionIds.Contains(x.Id) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var runtimeFormById = runtimeForms
            .GroupBy(form => form.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var expectedBindings = ownershipProof.Sum(branch => branch.BindingIds.Count);
        var receipts = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var outboxItems = await _ctx.DynamicFlowRuntimeOutbox
            .Find(x => x.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var events = await _ctx.DynamicFlowRuntimeEvents
            .Find(x => x.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var assignments = await _ctx.WorkAssignments
            .Find(x =>
                ((x.FlowInstanceId == instanceId && x.IsActive) ||
                 assignmentIds.Contains(x.Id)) &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var observedAssignmentIds = assignments
            .Select(item => item.Id)
            .Concat(assignmentIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var bindings = observedAssignmentIds.Count == 0
            ? new List<WorkTemplateAssignee>()
            : await _ctx.WorkTemplateAssignees
                .Find(x =>
                    observedAssignmentIds.Contains(x.WorkAssignmentId) &&
                    x.IsActive &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var periods = observedAssignmentIds.Count == 0
            ? new List<WorkReportPeriod>()
            : await _ctx.WorkReportPeriods
                .Find(x =>
                    observedAssignmentIds.Contains(x.WorkAssignmentId) &&
                    x.IsActive &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var reports = observedAssignmentIds.Count == 0
            ? new List<WorkAssignmentReport>()
            : await _ctx.WorkAssignmentReports
                .Find(x =>
                    observedAssignmentIds.Contains(x.WorkAssignmentId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var queues = observedAssignmentIds.Count == 0
            ? new List<WorkAssignmentQueueItem>()
            : await _ctx.WorkAssignmentQueueItems
                .Find(x =>
                    observedAssignmentIds.Contains(x.WorkAssignmentId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var assignmentDocRoles = observedAssignmentIds.Count == 0
            ? new List<DocRole>()
            : await _ctx.DocRoles
                .Find(x =>
                    x.DocType == DocType.WORK_ASSIGNMENT &&
                    observedAssignmentIds.Contains(x.DocId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var assignmentReadModels = observedAssignmentIds.Count == 0
            ? new List<AssignmentListDocRole>()
            : await _ctx.AssignmentListDocRoles
                .Find(x =>
                    observedAssignmentIds.Contains(x.AssignmentId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var workParticipantDocRoles = await _ctx.DocRoles
            .Find(x =>
                x.DocType == DocType.WORK &&
                x.DocId == instance.WorkId &&
                x.Role == DocRoleType.WORK_PARTICIPANT &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var workActiveAssignments = await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == instance.WorkId &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var periodIds = periods.Select(period => period.Id).ToList();
        var periodReadModels = periodIds.Count == 0
            ? new List<MyReportPeriodListDocRole>()
            : await _ctx.MyReportPeriodListDocRoles
                .Find(x => periodIds.Contains(x.WorkReportPeriodId) && !x.IsDeleted)
                .ToListAsync(ct);
        var workTemplateReadModels = await _ctx.MyReportTemplateListDocRoles
            .Find(x => x.WorkId == instance.WorkId && !x.IsDeleted)
            .ToListAsync(ct);
        var reviewReportReadModels = observedAssignmentIds.Count == 0
            ? new List<ReviewReportListDocRole>()
            : await _ctx.ReviewReportListDocRoles
                .Find(x =>
                    observedAssignmentIds.Contains(x.AssignmentId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var reviewSummaryReadModels = observedAssignmentIds.Count == 0
            ? new List<ReviewAssignmentSummaryDocRole>()
            : await _ctx.ReviewAssignmentSummaryDocRoles
                .Find(x =>
                    observedAssignmentIds.Contains(x.AssignmentId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var completed = outboxItems.Count(item =>
            item.Status == DynamicFlowRuntimeOutboxStatuses.Completed);
        var pending = outboxItems.Count - completed;

        var assignmentById = assignments
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var bindingById = bindings
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var periodById = periods
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var reportByIdForPeriodSource = reports
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var activeReportGroups = reports
            .Where(item => item.IsCurrent && item.IsActive)
            .GroupBy(item => item.WorkReportPeriodId, StringComparer.Ordinal)
            .ToList();
        var exactActiveReportCardinality =
            activeReportGroups.All(group => group.Count() == 1);
        var activeReportByPeriodId = activeReportGroups
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ThenByDescending(item => item.VersionNo)
                    .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        var projectionTriggerByPeriodId = reports
            .GroupBy(item => item.WorkReportPeriodId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.IsCurrent && item.IsActive)
                    .ThenByDescending(item => item.UpdatedAtUtc)
                    .ThenByDescending(item => item.VersionNo)
                    .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        var reportVersionCountByPeriodId = reports
            .GroupBy(report => report.WorkReportPeriodId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(report => report.VersionNo),
                StringComparer.Ordinal);
        var periodReadModelSources = periods
            .Where(period =>
                assignmentById.ContainsKey(period.WorkAssignmentId))
            .Select(period =>
            {
                bindingById.TryGetValue(
                    period.WorkTemplateAssigneeId,
                    out var sourceBinding);
                activeReportByPeriodId.TryGetValue(
                    period.Id,
                    out var sourceReport);
                return new DynamicFlowRuntimePeriodReadModelSource(
                    period,
                    assignmentById[period.WorkAssignmentId],
                    sourceBinding,
                    sourceReport);
            })
            .ToList();
        var workActiveAssignmentById = workActiveAssignments
            .GroupBy(assignment => assignment.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var workActiveAssignmentIds =
            workActiveAssignmentById.Keys.ToList();
        var workPeriods = workActiveAssignmentIds.Count == 0
            ? new List<WorkReportPeriod>()
            : await _ctx.WorkReportPeriods
                .Find(period =>
                    period.WorkId == instance.WorkId &&
                    workActiveAssignmentIds.Contains(
                        period.WorkAssignmentId) &&
                    period.IsActive &&
                    !period.IsDeleted)
                .ToListAsync(ct);
        var workBindingIds = workPeriods
            .Select(period => period.WorkTemplateAssigneeId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var workBindings = workBindingIds.Count == 0
            ? new List<WorkTemplateAssignee>()
            : await _ctx.WorkTemplateAssignees
                .Find(binding =>
                    workBindingIds.Contains(binding.Id) &&
                    binding.IsActive &&
                    !binding.IsDeleted)
                .ToListAsync(ct);
        var workPeriodIds = workPeriods
            .Select(period => period.Id)
            .ToList();
        var workReports = workPeriodIds.Count == 0
            ? new List<WorkAssignmentReport>()
            : await _ctx.WorkAssignmentReports
                .Find(report =>
                    workPeriodIds.Contains(report.WorkReportPeriodId) &&
                    !report.IsDeleted)
                .ToListAsync(ct);
        var workBindingById = workBindings
            .GroupBy(binding => binding.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var workActiveReportByPeriodId = workReports
            .Where(report => report.IsCurrent && report.IsActive)
            .GroupBy(
                report => report.WorkReportPeriodId,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(report => report.UpdatedAtUtc)
                    .ThenByDescending(report => report.VersionNo)
                    .ThenByDescending(
                        report => report.Id,
                        StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        var workPeriodReadModelSources = workPeriods
            .Where(period =>
                workActiveAssignmentById.ContainsKey(
                    period.WorkAssignmentId))
            .Select(period =>
            {
                workBindingById.TryGetValue(
                    period.WorkTemplateAssigneeId,
                    out var sourceBinding);
                workActiveReportByPeriodId.TryGetValue(
                    period.Id,
                    out var sourceReport);
                return new DynamicFlowRuntimePeriodReadModelSource(
                    period,
                    workActiveAssignmentById[
                        period.WorkAssignmentId],
                    sourceBinding,
                    sourceReport);
            })
            .ToList();
        var canonicalProgressByAssignmentId =
            new Dictionary<string, ProgressProjectionResult>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            canonicalProgressByAssignmentId[assignment.Id] =
                await _progress.ComputeProjectionAsync(assignment, ct);
        }
        var trustedCompletionAssignmentIds =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            var completionReceipts = receipts
                .Where(receipt =>
                    receipt.CommandType ==
                    DynamicFlowRuntimeStateProjectionOperations
                        .AssignmentComplete &&
                    receipt.CommandId ==
                    $"assignment-completed:{assignment.Id}")
                .ToArray();
            if (completionReceipts.Length == 1 &&
                TryReadAssignmentCompletionProjection(
                    completionReceipts[0],
                    out var completion) &&
                completion.AssignmentId == assignment.Id &&
                await IsTrustedAssignmentCompletionReceiptAsync(
                    completionReceipts[0],
                    assignment,
                    completion,
                    ct))
            {
                trustedCompletionAssignmentIds.Add(assignment.Id);
            }
        }
        var exactAssignments =
            ownershipProven &&
            assignments.Count == ownershipProof.Count &&
            ownershipProof.All(branch =>
                assignmentById.TryGetValue(branch.AssignmentId, out var assignment) &&
                runtimeFormById.TryGetValue(
                    branch.Step.FormVersionId,
                    out var runtimeForm) &&
                assignment.Id == branch.AssignmentId &&
                assignment.WorkId == instance.WorkId &&
                assignment.WorkType == instance.WorkType &&
                string.IsNullOrWhiteSpace(assignment.DynamicExcelId) &&
                assignment.DynamicExcelCode == string.Empty &&
                assignment.DynamicExcelName == string.Empty &&
                assignment.FlowInstanceId == instance.Id &&
                assignment.FlowTemplateId == instance.FlowTemplateId &&
                assignment.FlowTemplateVersionNo == instance.FlowTemplateVersionNo &&
                assignment.FlowStepId == branch.Step.FlowStepId &&
                assignment.FlowStepCode == branch.Step.FlowStepCode &&
                assignment.FlowStepOrder == branch.Step.StepOrder &&
                assignment.FlowBranchId == branch.BranchId &&
                assignment.FlowAttemptNo == branch.Step.AttemptNo &&
                assignment.FlowRole == DynamicFlowRuntimePlanner.AssignmentFlowRole &&
                assignment.FlowEffectiveStatus == DynamicFlowEffectiveStatuses.Effective &&
                assignment.DynamicFormTemplateId == branch.Step.FormVersionId &&
                assignment.DynamicFormTemplateCode == runtimeForm.Code &&
                assignment.DynamicFormTemplateName == runtimeForm.Name &&
                assignment.DynamicFormFamilyId == branch.Step.FormFamilyId &&
                assignment.DynamicFormVersionNo == branch.Step.FormVersionNo &&
                assignment.DynamicFormSchemaHash == branch.Step.FormSchemaHash &&
                assignment.IssuedByUnitId == instance.IssuerUnitId &&
                assignment.AssignmentType == WorkAssignmentTypes.Once &&
                assignment.AggregationType == WorkAggregationTypes.Matrix &&
                assignment.Schedule is null &&
                assignment.ParentAssignmentId is null &&
                assignment.RootAssignmentId == branch.AssignmentId &&
                assignment.Level == 0 &&
                assignment.Code ==
                $"DF-{instance.Id[..8]}-{branch.Step.AttemptNo}-{branch.Step.TargetUnitId[..6]}" &&
                assignment.Name ==
                (string.IsNullOrWhiteSpace(runtimeForm.Name)
                    ? branch.Step.FlowStepCode
                    : runtimeForm.Name) &&
                assignment.Path == $"/{branch.AssignmentId}" &&
                assignment.CreatedByUserId == instance.IssuerUserId &&
                (assignment.LeaderWatchers ?? new List<UserRef>()).Count == 0 &&
                assignment.ParentFlowBranchId ==
                (IsParallelFanOutArchetype(instance.ArchetypeId) &&
                 branch.Step.GatewayInstanceId is not null
                    ? DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        branch.Step.TargetUnitId)
                    : null) &&
                assignment.IsActive &&
                !assignment.IsDeleted &&
                assignment.DeletedAtUtc is null &&
                string.IsNullOrWhiteSpace(assignment.DeletedByUserId) &&
                MatchesAssignmentCompletionProjection(
                    assignment,
                    receipts,
                    trustedCompletionAssignmentIds.Contains(
                        assignment.Id)) &&
                canonicalProgressByAssignmentId.TryGetValue(
                    assignment.Id,
                    out var canonicalProgress) &&
                assignment.ProgressStatus == canonicalProgress.ProgressStatus &&
                assignment.HasAnyDuePeriod == canonicalProgress.HasAnyDuePeriod &&
                assignment.HasOverduePeriod == canonicalProgress.HasOverduePeriod &&
                assignment.LatestPeriodKey == canonicalProgress.LatestPeriodKey &&
                assignment.LatestDueAtUtc == canonicalProgress.LatestDueAtUtc &&
                assignment.WorstPeriodStatus == canonicalProgress.WorstPeriodStatus &&
                assignment.WorstOverdueReasonCode ==
                canonicalProgress.WorstOverdueReasonCode &&
                assignment.WorstOverdueReasonLabel ==
                canonicalProgress.WorstOverdueReasonLabel &&
                assignment.AllowSubFlow ==
                (instance.ArchetypeId ==
                 DynamicFlowSubflowTopologyContract.ArchetypeId) &&
                assignment.IsFlowFinalNode ==
                (instance.ArchetypeId is
                    DynamicFlowSequentialTopologyContract.ArchetypeId or
                    DynamicFlowParallelForkTopologyContract.ArchetypeId or
                    DynamicFlowJoinAllTopologyContract.ArchetypeId or
                    DynamicFlowJoinQuorumTopologyContract.ArchetypeId or
                    DynamicFlowTypedConditionalTopologyContract.ArchetypeId or
                    DynamicFlowReviewLoopTopologyContract.ArchetypeId or
                    DynamicFlowSubflowTopologyContract.ArchetypeId
                    ? branch.Step.IsTerminalNode
                    : true) &&
                (assignment.TargetUnitIds ?? new List<string>())
                    .SequenceEqual(new[] { branch.Step.TargetUnitId }) &&
                FrozenUsersMatch(
                    assignment.Assignees ?? new List<UserRef>(),
                    branch.Binding.Participants));
        var exactBindings =
            ownershipProven &&
            bindings.Count == expectedBindings &&
            ownershipProof.All(branch =>
                branch.Binding.Participants.All(participant =>
                {
                    var bindingId = StableObjectId(
                        $"{branch.AssignmentId}\nbinding\n{participant.UserId}");
                    return bindingById.TryGetValue(bindingId, out var binding) &&
                           runtimeFormById.TryGetValue(
                               branch.Step.FormVersionId,
                               out var runtimeForm) &&
                           binding.Id == bindingId &&
                           binding.WorkId == instance.WorkId &&
                           binding.WorkAssignmentId == branch.AssignmentId &&
                           binding.AssigneeUserId == participant.UserId &&
                           binding.AssigneeUsername == participant.Username &&
                           binding.AssigneeFullName == participant.FullName &&
                           binding.AssigneeUnitId == participant.UnitId &&
                           binding.AssigneeUnitSymbol == participant.UnitSymbol &&
                           binding.AssigneeUnitShortName == participant.UnitShortName &&
                           binding.AssigneeUnitName == participant.UnitName &&
                           binding.IsActive &&
                           !binding.IsDeleted &&
                           binding.DeletedAtUtc is null &&
                           string.IsNullOrWhiteSpace(binding.DeletedByUserId) &&
                           binding.AssignmentType == WorkAssignmentTypes.Once &&
                           binding.AggregationType == WorkAggregationTypes.Matrix &&
                           binding.Schedule is null &&
                           binding.DynamicFormTemplateId == branch.Step.FormVersionId &&
                           binding.DynamicFormTemplateCode == runtimeForm.Code &&
                           binding.DynamicFormTemplateName == runtimeForm.Name &&
                           binding.DynamicFormFamilyId == branch.Step.FormFamilyId &&
                           binding.DynamicFormVersionNo == branch.Step.FormVersionNo &&
                           binding.DynamicFormSchemaHash == branch.Step.FormSchemaHash;
                }));
        var exactPeriods = exactActiveReportCardinality &&
                           periods.Count == expectedBindings &&
                           periods.All(period =>
                               bindingById.TryGetValue(period.WorkTemplateAssigneeId, out var binding) &&
                               period.Id == StableObjectId(
                                   $"{instance.Id}\nperiod\n{binding.WorkAssignmentId}\n{binding.AssigneeUserId}\n{instance.PeriodKey}") &&
                               period.WorkId == instance.WorkId &&
                               period.WorkAssignmentId == binding.WorkAssignmentId &&
                               period.AssigneeUserId == binding.AssigneeUserId &&
                               period.AssigneeUnitId == binding.AssigneeUnitId &&
                               string.IsNullOrWhiteSpace(period.DynamicExcelId) &&
                               period.DynamicExcelCode == string.Empty &&
                               period.DynamicExcelName == string.Empty &&
                               period.DynamicFormTemplateId == binding.DynamicFormTemplateId &&
                               period.DynamicFormFamilyId == binding.DynamicFormFamilyId &&
                               period.DynamicFormVersionNo == binding.DynamicFormVersionNo &&
                               period.DynamicFormSchemaHash == binding.DynamicFormSchemaHash &&
                               period.DynamicFormTemplateCode ==
                               binding.DynamicFormTemplateCode &&
                               period.DynamicFormTemplateName ==
                               binding.DynamicFormTemplateName &&
                               period.ReportTitle ==
                               binding.DynamicFormTemplateName &&
                               period.PeriodKey == instance.PeriodKey &&
                               period.PeriodInstanceKey == instance.PeriodKey &&
                               period.PeriodKind ==
                               WorkReportPeriodKind.Scheduled &&
                               period.ReportVersionCount ==
                               (reportVersionCountByPeriodId.TryGetValue(
                                   period.Id,
                                   out var expectedReportVersionCount)
                                   ? expectedReportVersionCount
                                   : 0) &&
                               MatchesCanonicalPeriodLifecycle(
                                   period,
                                   activeReportByPeriodId,
                                   projectionTriggerByPeriodId) &&
                               period.CurrentReportId ==
                               (activeReportByPeriodId.TryGetValue(
                                   period.Id,
                                   out var activeReport)
                                   ? activeReport.Id
                                   : null) &&
                                (activeReportByPeriodId.TryGetValue(
                                    period.Id,
                                    out var activeLifecycleSource)
                                    ? activeLifecycleSource.LifecycleRevision > 0
                                        ? period.SourceLifecycleReportId ==
                                          activeLifecycleSource.Id &&
                                          period.SourceLifecycleRevision ==
                                          activeLifecycleSource.LifecycleRevision &&
                                          period.SourceLifecycleAppliedAtUtc.HasValue
                                        : string.IsNullOrWhiteSpace(
                                            period.SourceLifecycleReportId)
                                            ? period.SourceLifecycleRevision == 0 &&
                                              period.SourceLifecycleAppliedAtUtc is null
                                            : reportByIdForPeriodSource.TryGetValue(
                                                  period.SourceLifecycleReportId,
                                                  out var priorLifecycleSource) &&
                                              priorLifecycleSource.WorkReportPeriodId ==
                                              period.Id &&
                                              priorLifecycleSource.WorkAssignmentId ==
                                              period.WorkAssignmentId &&
                                              period.SourceLifecycleRevision ==
                                              priorLifecycleSource.LifecycleRevision &&
                                              period.SourceLifecycleAppliedAtUtc.HasValue
                                    : string.IsNullOrWhiteSpace(
                                        period.SourceLifecycleReportId)
                                       ? period.SourceLifecycleRevision == 0 &&
                                         period.SourceLifecycleAppliedAtUtc is null &&
                                         reports.All(report =>
                                             report.WorkReportPeriodId != period.Id)
                                       : reportByIdForPeriodSource.TryGetValue(
                                             period.SourceLifecycleReportId,
                                             out var inactiveLifecycleSource) &&
                                         inactiveLifecycleSource.WorkReportPeriodId ==
                                         period.Id &&
                                         inactiveLifecycleSource.WorkAssignmentId ==
                                         period.WorkAssignmentId &&
                                         (!inactiveLifecycleSource.IsCurrent ||
                                          !inactiveLifecycleSource.IsActive) &&
                                         period.SourceLifecycleRevision ==
                                         inactiveLifecycleSource.LifecycleRevision &&
                                         period.SourceLifecycleAppliedAtUtc.HasValue) &&
                               period.IsActive &&
                               !period.IsDeleted &&
                               period.DeletedAtUtc is null &&
                               string.IsNullOrWhiteSpace(
                                   period.DeletedByUserId));
        var exactQueues = queues.Count == expectedBindings &&
                           periods.All(period =>
                                queues.Count(queue =>
                                    queue.WorkId == period.WorkId &&
                                    queue.WorkAssignmentId == period.WorkAssignmentId &&
                                    queue.AssigneeUserId == period.AssigneeUserId &&
                                    queue.PeriodKey == period.PeriodKey &&
                                    queue.DueAtUtc == period.DueAtUtc &&
                                    queue.LastObservedPeriodStatus ==
                                    (int)period.Status &&
                                    (period.DueAtUtc.HasValue
                                        ? queue.NextScanAtUtc ==
                                          period.DueAtUtc.Value
                                        : queue.NextScanAtUtc >=
                                          period.UpdatedAtUtc &&
                                          queue.NextScanAtUtc <=
                                          DateTime.UtcNow.AddHours(6)) &&
                                     queue.IsActive ==
                                     (assignmentById.TryGetValue(
                                          period.WorkAssignmentId,
                                          out var queueAssignment) &&
                                      !queueAssignment.CompletedAtUtc.HasValue &&
                                      period.IsActive &&
                                      !period.IsHistoricalData &&
                                      WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status)) &&
                                    !queue.IsDeleted) == 1);
        var expectedAssignmentRoleCount = steps.Sum(step => step.ParticipantUserIds.Count + 1);
        var exactDocRoles =
            assignmentDocRoles.Count == expectedAssignmentRoleCount &&
            assignmentDocRoles.All(role =>
                role.DocType == DocType.WORK_ASSIGNMENT &&
                !role.IsDeleted &&
                role.DeletedAtUtc is null &&
                string.IsNullOrWhiteSpace(role.DeletedByUserId)) &&
            ownershipProof.All(branch =>
                assignmentDocRoles.Count(role =>
                    role.DocId == branch.AssignmentId &&
                    role.UserId == instance.IssuerUserId &&
                    role.Role == DocRoleType.ASSIGNER &&
                    role.User is null) == 1 &&
                branch.Binding.Participants.All(participant =>
                    assignmentDocRoles.Count(role =>
                        role.DocId == branch.AssignmentId &&
                        role.UserId == participant.UserId &&
                        role.Role == DocRoleType.ASSIGNEE &&
                        FrozenUserMatches(
                            role.User,
                            participant)) == 1));
        var expectedWorkParticipantUsers = workActiveAssignments
            .SelectMany(assignment =>
                assignment.Assignees ?? new List<UserRef>())
            .Where(user => !string.IsNullOrWhiteSpace(user.UserId))
            .GroupBy(user => user.UserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToArray();
        var exactWorkParticipantRoles =
            workParticipantDocRoles.Count ==
            expectedWorkParticipantUsers.Length &&
            workParticipantDocRoles.All(role =>
                role.DocType == DocType.WORK &&
                role.DocId == instance.WorkId &&
                role.Role == DocRoleType.WORK_PARTICIPANT &&
                !role.IsDeleted &&
                role.DeletedAtUtc is null &&
                string.IsNullOrWhiteSpace(role.DeletedByUserId)) &&
            expectedWorkParticipantUsers.All(user =>
                workParticipantDocRoles.Count(role =>
                    role.UserId == user.UserId &&
                    UserRefsMatch(role.User, user)) == 1);
        var expectedAssignmentReadModels = steps.Sum(step =>
            step.ParticipantUserIds.Append(instance.IssuerUserId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        var expectedAssignmentReadModelRoles = assignmentDocRoles
            .GroupBy(
                role => $"{role.DocId}\n{role.UserId}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(role => role.Role).ToHashSet(),
                StringComparer.Ordinal);
        var exactAssignmentReadModels =
            assignmentReadModels.Count == expectedAssignmentReadModels &&
            expectedAssignmentReadModelRoles.Count == expectedAssignmentReadModels &&
            assignmentReadModels.All(readModel =>
                assignmentById.TryGetValue(readModel.AssignmentId, out var assignment) &&
                expectedAssignmentReadModelRoles.TryGetValue(
                    $"{readModel.AssignmentId}\n{readModel.UserId}",
                    out var expectedRoles) &&
                DynamicFlowRuntimeReadModelContract.MatchesAssignment(
                    readModel,
                    assignment,
                    readModel.UserId,
                    expectedRoles,
                    DynamicFlowRuntimeReadModelContract.ResolveAssignmentUser(
                        assignment,
                        readModel.UserId)));
        var periodReadModelSourceById = periodReadModelSources
            .GroupBy(source => source.Period.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var exactPeriodReadModels =
            periodReadModels.Count == expectedBindings &&
            periodReadModels.All(readModel =>
                periodReadModelSourceById.TryGetValue(
                    readModel.WorkReportPeriodId,
                    out var source) &&
                DynamicFlowRuntimeReadModelContract.MatchesMyReportPeriod(
                    readModel,
                    source));
        var currentTemplateKeys = periodReadModelSources
            .Select(source =>
                $"{source.Period.WorkId}\n{source.Period.DynamicFormTemplateId}\n{source.Period.AssigneeUserId}")
            .ToHashSet(StringComparer.Ordinal);
        var templateReadModels = workTemplateReadModels
            .Where(readModel =>
                currentTemplateKeys.Contains(
                    $"{readModel.WorkId}\n{readModel.DynamicFormTemplateId}\n{readModel.UserId}"))
            .ToList();
        var expectedTemplateReadModelCount = workPeriodReadModelSources
            .GroupBy(
                source =>
                    $"{source.Period.WorkId}\n{source.Period.DynamicFormTemplateId}\n{source.Period.AssigneeUserId}",
                StringComparer.Ordinal)
            .Count(group => currentTemplateKeys.Contains(group.Key));
        var exactTemplateReadModels =
            templateReadModels.Count == expectedTemplateReadModelCount &&
            templateReadModels.All(readModel =>
                DynamicFlowRuntimeReadModelContract.MatchesMyReportTemplate(
                    readModel,
                    workPeriodReadModelSources));
        var expectedReviewReportReadModelCount = periodReadModelSources.Count(
            source =>
                !string.IsNullOrWhiteSpace(
                    source.Assignment.CreatedByUserId));
        var exactReviewReportReadModels =
            reviewReportReadModels.Count ==
            expectedReviewReportReadModelCount &&
            reviewReportReadModels.All(readModel =>
                periodReadModelSourceById.TryGetValue(
                    readModel.WorkReportPeriodId,
                    out var source) &&
                DynamicFlowRuntimeReadModelContract.MatchesReviewReport(
                    readModel,
                    source));
        var expectedReviewSummaryReadModelCount = periodReadModelSources
            .Where(source =>
                !string.IsNullOrWhiteSpace(
                    source.Assignment.CreatedByUserId))
            .GroupBy(
                source =>
                    $"{source.Period.WorkAssignmentId}\n{source.Assignment.CreatedByUserId}",
                StringComparer.Ordinal)
            .Count();
        var exactReviewSummaryReadModels =
            reviewSummaryReadModels.Count ==
            expectedReviewSummaryReadModelCount &&
            reviewSummaryReadModels.All(readModel =>
                DynamicFlowRuntimeReadModelContract
                    .MatchesReviewAssignmentSummary(
                        readModel,
                        periodReadModelSources));
        var launchReceipts = receipts
            .Where(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                receipt.CommandType == "LAUNCH")
            .ToList();
        var stateReceipts = receipts
            .Where(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                receipt.CommandType is
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle or
                    DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete or
                    DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
            .ToList();
        var forwardReceipts = receipts
            .Where(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                receipt.CommandType == "FORWARD")
            .ToList();
        var exactStateReceiptKinds =
            launchReceipts.Count +
            stateReceipts.Count +
            forwardReceipts.Count == receipts.Count;
        var exactLaunchReceipt =
            ownershipProven &&
            participantSnapshot is not null &&
            launchReceipts.Count == 1 &&
            ValidateRuntimeReceipt(
                instance,
                participantSnapshot,
                ownershipProof,
                launchReceipts[0],
                outboxItems);
        var exactOutbox =
            ownershipProven &&
            participantSnapshot is not null &&
            ValidateRuntimeOutbox(
                instance,
                participantSnapshot,
                ownershipProof,
                outboxItems,
                forwardReceipts);
        var stateEvents = events
            .Where(item =>
                DynamicFlowRuntimeStateProjectionEventTypes.All.Contains(item.EventType))
            .ToList();
        var baseEvents = events
            .Where(item =>
                !DynamicFlowRuntimeStateProjectionEventTypes.All.Contains(item.EventType))
            .ToList();
        var exactGlobalEventSequence =
            events.Count > 0 &&
            instance.NextEventSequence == events.Count + 1L &&
            events.Select(item => item.Sequence)
                .OrderBy(value => value)
                .SequenceEqual(
                    Enumerable.Range(1, events.Count).Select(value => (long)value));
        var exactBaseEvents =
            ownershipProven &&
            launchReceipts.Count == 1 &&
            ValidateRuntimeBaseEvents(
                instance,
                ownershipProof,
                launchReceipts[0],
                forwardReceipts,
                outboxItems,
                baseEvents);
        var exactStateEvents =
            ownershipProven &&
            exactStateReceiptKinds &&
            ValidateRuntimeStateProjectionLedger(
                instance,
                ownershipProof,
                stateReceipts,
                stateEvents,
                baseEvents,
                reports);
        var exactMaterializationPinsAndRefs =
            ownershipProven &&
            exactLaunchReceipt &&
            exactOutbox &&
            exactBaseEvents &&
            exactAssignments &&
            exactBindings &&
            exactPeriods &&
            exactQueues &&
            exactDocRoles &&
            exactWorkParticipantRoles &&
            exactAssignmentReadModels &&
            exactPeriodReadModels &&
            exactTemplateReadModels &&
            exactReviewReportReadModels &&
            exactReviewSummaryReadModels;
        var stableStepStates = ownershipProof.All(branch =>
            branch.Step.State is
                DynamicFlowStepStates.Assigned or
                DynamicFlowStepStates.InProgress or
                DynamicFlowStepStates.Submitted or
                DynamicFlowStepStates.Returned or
                DynamicFlowStepStates.Approved or
                DynamicFlowStepStates.Completed or
                DynamicFlowStepStates.WaitingChild or
                DynamicFlowStepStates.Failed or
                DynamicFlowStepStates.Terminated);
        var expectedInstanceState = ExpectedRuntimeInstanceState(
            instance,
            ownershipProof.Select(branch => branch.Step).ToArray());
        var exactMaterializationLedger =
            steps.Count > 0 &&
            ownershipProof.Count == steps.Count &&
            ownershipProof.All(branch =>
                branch.Step.AssignmentId == branch.AssignmentId) &&
            stableStepStates &&
            exactMaterializationPinsAndRefs &&
            completed == ownershipProof.Count &&
            pending == 0;
        var exactStateProjectionLedger =
            exactStateReceiptKinds &&
            exactStateEvents &&
            exactGlobalEventSequence &&
            instance.State == expectedInstanceState;
        var exactPinsAndRefs =
            exactMaterializationPinsAndRefs &&
            exactStateReceiptKinds &&
            exactStateEvents;
        var exactLedger =
            exactMaterializationLedger &&
            exactStateProjectionLedger;
        if (!exactMaterializationLedger)
        {
            _logger.LogWarning(
                "Dynamic Flow materialization oracle mismatch. instanceId={instanceId} ownership={ownership} assignments={assignments} bindings={bindings} periods={periods} queues={queues} docRoles={docRoles} workRoles={workRoles} assignmentReadModels={assignmentReadModels} periodReadModels={periodReadModels} templateReadModels={templateReadModels} reviewReportReadModels={reviewReportReadModels} reviewSummaryReadModels={reviewSummaryReadModels} launchReceipt={launchReceipt} outbox={outbox} baseEvents={baseEvents} stableSteps={stableSteps} completed={completed}/{expected}",
                instanceId,
                ownershipProven,
                exactAssignments,
                exactBindings,
                exactPeriods,
                exactQueues,
                exactDocRoles,
                exactWorkParticipantRoles,
                exactAssignmentReadModels,
                exactPeriodReadModels,
                exactTemplateReadModels,
                exactReviewReportReadModels,
                exactReviewSummaryReadModels,
                exactLaunchReceipt,
                exactOutbox,
                exactBaseEvents,
                stableStepStates,
                completed,
                ownershipProof.Count);
        }
        return new DynamicFlowRuntimeReconcileResult
        {
            FlowInstanceId = instanceId,
            Apply = apply,
            StateBefore = stateBefore,
            StateAfter = instance.State,
            ExpectedBranches = steps.Count,
            AssignmentCount = assignments.Count,
            BindingCount = bindings.Count,
            PeriodCount = periods.Count,
            QueueCount = queues.Count,
            AssignmentDocRoleCount = assignmentDocRoles.Count,
            WorkParticipantDocRoleCount = workParticipantDocRoles.Count,
            AssignmentReadModelCount = assignmentReadModels.Count,
            PeriodReadModelCount = periodReadModels.Count,
            CompletedOutboxCount = completed,
            PendingOutboxCount = pending,
            ExactPinsAndRefs = exactPinsAndRefs,
            ExactMaterializationLedgerConverged = exactMaterializationLedger,
            ExactStateProjectionLedgerConverged = exactStateProjectionLedger,
            ExactLedgerConverged = exactLedger,
            Converged = exactLedger
        };
    }

    private static string ExpectedRuntimeInstanceState(
        DynamicFlowInstance instance,
        IReadOnlyList<DynamicFlowStepInstance> steps)
    {
        if (instance.State is
            DynamicFlowInstanceStates.Failed or
            DynamicFlowInstanceStates.Terminated)
        {
            return instance.State;
        }
        var allMaterializedStepsCompleted =
            steps.Count > 0 &&
            (instance.ArchetypeId ==
                DynamicFlowSupplementalTopologyContract.ArchetypeId
                ? DynamicFlowSupplementalTopologyContract
                    .CompletionSatisfied(steps)
                : steps.All(step =>
                    step.State == DynamicFlowStepStates.Completed));
        var everySequentialBranchReachedTerminal =
            !string.Equals(
                instance.ArchetypeId,
                DynamicFlowSequentialTopologyContract.ArchetypeId,
                StringComparison.Ordinal) ||
            steps
                .GroupBy(step => step.BranchId, StringComparer.Ordinal)
                .All(branch =>
                    branch.Any(step =>
                        step.IsTerminalNode &&
                        step.State == DynamicFlowStepStates.Completed));
        return allMaterializedStepsCompleted &&
               everySequentialBranchReachedTerminal
            ? DynamicFlowInstanceStates.Completed
            : DynamicFlowInstanceStates.Active;
    }

    private async Task UpsertBranchCompletedEventAsync(
        IClientSessionHandle session,
        DynamicFlowRuntimeOutboxItem item,
        string assignmentId,
        DateTime occurredAtUtc,
        CancellationToken ct)
    {
        var ordinal = item.Payload["branchOrdinal"].AsInt32;
        var commandId = item.Payload["commandId"].AsString;
        var sequential =
            item.Operation ==
            DynamicFlowRuntimeMaterializationOperations.MaterializeSequentialAssignment;
        var forkBranch =
            item.Operation ==
            DynamicFlowRuntimeMaterializationOperations.MaterializeForkBranchAssignment;
        var reviewAttempt =
            item.Operation ==
            DynamicFlowRuntimeMaterializationOperations
                .MaterializeReviewAttemptAssignment;
        var supplemental =
            item.Operation ==
            DynamicFlowRuntimeMaterializationOperations
                .MaterializeSupplementalAssignment;
        var topologyForward = sequential || forkBranch || reviewAttempt;
        var eventId = topologyForward
            ? StableObjectId(
                $"{item.FlowInstanceId}\nevent\n{(reviewAttempt ? "review-attempt" : forkBranch ? "fork" : "sequential")}-materialized\n{item.StepInstanceId}")
            : supplemental
                ? StableObjectId(
                    $"{item.FlowInstanceId}\nevent\nsupplemental-materialized\n{item.StepInstanceId}")
            : StableObjectId($"{item.FlowInstanceId}\nevent\nbranch\n{ordinal}");
        var sequence = (topologyForward || supplemental) &&
                       item.Payload.TryGetValue(
                           "materializationEventSequence",
                           out var reservedSequence)
            ? reservedSequence.ToInt64()
            : ordinal + 1L;
        var payload = new BsonDocument
        {
            { "assignmentId", assignmentId },
            { "targetUnitId", item.Payload["targetUnitId"].AsString },
            { "branchOrdinal", ordinal },
            {
                "branchId",
                item.Payload.TryGetValue("branchId", out var payloadBranchId)
                    ? payloadBranchId
                    : BsonNull.Value
            },
            {
                "attemptNo",
                item.Payload.TryGetValue("attemptNo", out var payloadAttemptNo)
                    ? payloadAttemptNo
                    : BsonNull.Value
            },
            {
                "executionEpoch",
                item.Payload.TryGetValue("executionEpoch", out var executionEpoch)
                    ? executionEpoch
                    : BsonNull.Value
            },
            {
                "transitionId",
                item.Payload.TryGetValue("transitionId", out var transitionId)
                    ? transitionId
                    : BsonNull.Value
            },
            {
                "gatewayInstanceId",
                item.Payload.TryGetValue("gatewayInstanceId", out var gatewayInstanceId)
                    ? gatewayInstanceId
                    : BsonNull.Value
            },
            {
                "gatewayVersion",
                item.Payload.TryGetValue("gatewayVersion", out var gatewayVersion)
                    ? gatewayVersion
                    : BsonNull.Value
            },
            {
                "contributionId",
                item.Payload.TryGetValue("contributionId", out var contributionId)
                    ? contributionId
                    : BsonNull.Value
            }
        };
        var visibleUnits = new[]
        {
            item.Payload["targetUnitId"].AsString,
            item.Payload["issuerUnitId"].AsString
        }.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal);
        await _ctx.DynamicFlowRuntimeEvents.UpdateOneAsync(
            session,
            x => x.Id == eventId,
            Builders<DynamicFlowRuntimeEvent>.Update
                .SetOnInsert(x => x.Id, eventId)
                .SetOnInsert(x => x.FlowInstanceId, item.FlowInstanceId)
                .SetOnInsert(x => x.StepInstanceId, item.StepInstanceId)
                .SetOnInsert(
                    x => x.ExecutionEpoch,
                    item.Payload.TryGetValue("executionEpoch", out var epoch)
                        ? epoch.AsInt32
                        : 1)
                .SetOnInsert(
                    x => x.BranchId,
                    item.Payload.TryGetValue("branchId", out var branchId) &&
                    branchId.IsString
                        ? branchId.AsString
                        : null)
                .SetOnInsert(
                    x => x.AttemptNo,
                    item.Payload.TryGetValue("attemptNo", out var attemptNo) &&
                    attemptNo.IsInt32
                        ? attemptNo.AsInt32
                        : null)
                .SetOnInsert(
                    x => x.GatewayInstanceId,
                    item.Payload.TryGetValue("gatewayInstanceId", out var gatewayId) &&
                    gatewayId.IsString
                        ? gatewayId.AsString
                        : null)
                .SetOnInsert(
                    x => x.GatewayVersion,
                    item.Payload.TryGetValue("gatewayVersion", out var version) &&
                    version.IsInt32
                        ? version.AsInt32
                        : null)
                .SetOnInsert(
                    x => x.ContributionId,
                    item.Payload.TryGetValue("contributionId", out var contribution) &&
                    contribution.IsString
                        ? contribution.AsString
                        : null)
                .SetOnInsert(x => x.Sequence, sequence)
                .SetOnInsert(
                    x => x.EventType,
                    reviewAttempt
                        ? DynamicFlowRuntimeEventTypes
                            .ReviewAttemptAssignmentMaterialized
                        : forkBranch
                        ? DynamicFlowRuntimeEventTypes.ForkBranchAssignmentMaterialized
                        : sequential
                            ? DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized
                            : supplemental
                                ? DynamicFlowRuntimeEventTypes
                                    .SupplementalAssignmentMaterialized
                            : DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized)
                .SetOnInsert(x => x.CommandId, commandId)
                .SetOnInsert(x => x.ActorUserId, item.Payload["actorUserId"].AsString)
                .SetOnInsert(x => x.VisibleUnitIds, visibleUnits.ToList())
                .SetOnInsert(x => x.Payload, payload)
                .SetOnInsert(x => x.PayloadHash, Hash(payload.ToJson()))
                .SetOnInsert(x => x.OccurredAtUtc, occurredAtUtc),
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    private async Task AppendRecoveryEventAsync(
        string instanceId,
        string eventType,
        string actorUserId,
        CancellationToken ct,
        string? runtimeReconcileLeaseId = null)
    {
        var commandId = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == instanceId)
            .Project(x => x.LaunchCommandId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.BeforeRecoveryEventWrite);
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var instance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        RuntimeMaterializationFenceFilter(
                            instanceId,
                            runtimeReconcileLeaseId,
                            DateTime.UtcNow))
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECOVERY_EVENT_FENCE_CONFLICT");
                var eventId = RecoveryEventId(
                    instanceId,
                    eventType,
                    instance.RuntimeRecoveryEpoch);
                var existing = await _ctx.DynamicFlowRuntimeEvents
                    .Find(session, x => x.Id == eventId)
                    .FirstOrDefaultAsync(transactionCt);
                var sequence = existing?.Sequence ?? instance.NextEventSequence;
                var instanceUpdate =
                    Builders<DynamicFlowInstance>.Update.Inc(
                        x => x.RuntimeMaterializationFenceRevision,
                        1);
                if (existing is null)
                {
                    instanceUpdate = instanceUpdate.Inc(
                            x => x.NextEventSequence,
                            1);
                }
                var instanceFilter = RuntimeMaterializationFenceFilter(
                        instanceId,
                        runtimeReconcileLeaseId,
                        DateTime.UtcNow) &
                    Builders<DynamicFlowInstance>.Filter.Eq(
                        x => x.Revision,
                        instance.Revision);
                if (existing is null)
                {
                    instanceFilter &=
                        Builders<DynamicFlowInstance>.Filter.Eq(
                            x => x.NextEventSequence,
                            instance.NextEventSequence);
                }
                var reserved = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    instanceFilter,
                    instanceUpdate,
                    cancellationToken: transactionCt);
                if (reserved.ModifiedCount != 1)
                {
                    throw new DynamicFlowRuntimeReconcileFenceException(
                        "DYNAMIC_FLOW_RUNTIME_RECOVERY_EVENT_SEQUENCE_CONFLICT");
                }
                await UpsertRecoveryEventAsync(
                    session,
                    instance,
                    eventType,
                    actorUserId,
                    sequence,
                    instance.State,
                    DateTime.UtcNow,
                    transactionCt);
            },
            ct);
        _faults.ThrowIfConfigured(
            commandId,
            0,
            DynamicFlowRuntimeFaultPoints.AfterRecoveryEventWrite);
    }

    private async Task UpsertRecoveryEventAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        string eventType,
        string actorUserId,
        long sequence,
        string state,
        DateTime occurredAtUtc,
        CancellationToken ct)
    {
        var eventId = RecoveryEventId(
            instance.Id,
            eventType,
            instance.RuntimeRecoveryEpoch);
        var payload = new BsonDocument
        {
            { "state", state },
            { "recoveryEpoch", instance.RuntimeRecoveryEpoch }
        };
        var eventUpdate = Builders<DynamicFlowRuntimeEvent>.Update
            .SetOnInsert(x => x.Id, eventId)
            .SetOnInsert(x => x.FlowInstanceId, instance.Id)
            .SetOnInsert(x => x.StepInstanceId, null)
            .SetOnInsert(x => x.Sequence, sequence)
            .SetOnInsert(x => x.EventType, eventType)
            .SetOnInsert(x => x.CommandId, instance.LaunchCommandId)
            .SetOnInsert(x => x.ActorUserId, instance.IssuerUserId)
            .SetOnInsert(
                x => x.VisibleUnitIds,
                new List<string> { instance.IssuerUnitId })
            .SetOnInsert(x => x.Payload, payload)
            .SetOnInsert(x => x.PayloadHash, Hash(payload.ToJson()))
            .SetOnInsert(x => x.OccurredAtUtc, occurredAtUtc);
        await _ctx.DynamicFlowRuntimeEvents.UpdateOneAsync(
            session,
            x => x.Id == eventId,
            eventUpdate,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    private static string RecoveryEventId(
        string instanceId,
        string eventType,
        long recoveryEpoch)
        => StableObjectId(
            $"{instanceId}\nrecovery\n{recoveryEpoch}\n{eventType}");

    private Task<DynamicFlowRuntimeCommandReceipt?> FindReceiptAsync(
        string scopeId,
        string commandId,
        CancellationToken ct)
        => _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x =>
                x.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                x.ScopeId == scopeId &&
                x.CommandType == "LAUNCH" &&
                x.CommandId == commandId)
            .FirstOrDefaultAsync(ct);

    private static RuntimeIntent BuildIntent(
        DynamicFlowPreflightResponse preflight,
        string actorUserId,
        DynamicFlowSubflowLaunchContext? subflowContext = null,
        DynamicFlowPeriodicLaunchContext? periodicContext = null)
    {
        var now = DateTime.UtcNow;
        var sequentialTopology =
            string.Equals(
                preflight.FlowPin.ArchetypeId,
                DynamicFlowSequentialTopologyContract.ArchetypeId,
                StringComparison.Ordinal)
                ? DynamicFlowSequentialTopologyContract.Require(
                    preflight.LockedDefinitionJson,
                    preflight.FlowPin.TopologyHash)
                : null;
        var parallelForkTopology = IsParallelFanOutArchetype(
                preflight.FlowPin.ArchetypeId)
            ? RequireParallelFanOutTopology(
                preflight.FlowPin.ArchetypeId,
                preflight.LockedDefinitionJson,
                preflight.FlowPin.TopologyHash)
            : null;
        var reviewLoopTopology =
            string.Equals(
                preflight.FlowPin.ArchetypeId,
                DynamicFlowReviewLoopTopologyContract.ArchetypeId,
                StringComparison.Ordinal)
                ? DynamicFlowReviewLoopTopologyContract.Require(
                    preflight.LockedDefinitionJson,
                    preflight.FlowPin.TopologyHash)
                : null;
        var finalizeTopology =
            string.Equals(
                preflight.FlowPin.ArchetypeId,
                DynamicFlowFinalizeTopologyContract.ArchetypeId,
                StringComparison.Ordinal)
                ? DynamicFlowFinalizeTopologyContract.Require(
                    preflight.LockedDefinitionJson,
                    preflight.FlowPin.TopologyHash)
                : null;
        var instanceId = periodicContext is not null
            ? DynamicFlowPeriodicTopologyContract.BuildInstanceId(
                periodicContext.ScheduleId,
                periodicContext.PeriodKey)
            : subflowContext is null
            ? StableObjectId(
                $"{preflight.WorkId}\n{preflight.FlowPin.FlowTemplateVersionId}\n{preflight.CommandId}")
            : DynamicFlowSubflowTopologyContract.BuildChildInstanceId(
                preflight.WorkId,
                preflight.FlowPin.FlowTemplateVersionId,
                preflight.CommandId);
        var snapshotId = StableObjectId($"{instanceId}\nparticipants");
        var entryForm = preflight.FormPins.SingleOrDefault(pin =>
                            string.Equals(
                                pin.FormNodeId,
                                preflight.EntryStep.FormNodeId,
                                StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("DYNAMIC_FLOW_ENTRY_FORM_PIN_MISSING");
        var branches = preflight.Targets
            .OrderBy(target => target.TargetUnitId, StringComparer.Ordinal)
            .Select((target, index) =>
            {
                var ordinal = index + 1;
                var branchId = finalizeTopology is not null
                    ? DynamicFlowFinalizeTopologyContract.BuildBranchId(
                        instanceId,
                        DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                        target.TargetUnitId)
                    : reviewLoopTopology is not null
                    ? DynamicFlowReviewLoopTopologyContract.BuildBranchId(
                        instanceId,
                        DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                        target.TargetUnitId)
                    : sequentialTopology is null && parallelForkTopology is null
                    ? StableObjectId(
                        $"{instanceId}\n{preflight.EntryStep.StepId}\n{target.TargetUnitId}\n1\nbranch")
                    : sequentialTopology is not null
                        ? DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                            instanceId,
                            DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                            target.TargetUnitId)
                        : DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                            instanceId,
                            DynamicFlowParallelForkTopologyContract.InitialExecutionEpoch,
                            target.TargetUnitId);
                var stepId = finalizeTopology is not null
                    ? DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
                        instanceId,
                        DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                        preflight.EntryStep.StepId,
                        branchId)
                    : reviewLoopTopology is not null
                    ? DynamicFlowReviewLoopTopologyContract.BuildStepInstanceId(
                        instanceId,
                        DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                        preflight.EntryStep.StepId,
                        branchId,
                        1)
                    : sequentialTopology is null && parallelForkTopology is null
                    ? StableObjectId(
                        $"{instanceId}\n{preflight.EntryStep.StepId}\n{target.TargetUnitId}\n1\nstep")
                    : DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                        instanceId,
                        DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                        preflight.EntryStep.StepId,
                        branchId,
                        1);
                var assignmentId = finalizeTopology is not null
                    ? DynamicFlowFinalizeTopologyContract.BuildAssignmentId(
                        stepId)
                    : reviewLoopTopology is not null
                    ? DynamicFlowReviewLoopTopologyContract.BuildAssignmentId(stepId)
                    : sequentialTopology is null && parallelForkTopology is null
                    ? StableObjectId($"{branchId}\nassignment")
                    : DynamicFlowSequentialTopologyContract.BuildAssignmentId(stepId);
                var step = new DynamicFlowStepInstance
                {
                    Id = stepId,
                    FlowInstanceId = instanceId,
                    FlowStepId = preflight.EntryStep.StepId,
                    FlowStepCode = preflight.EntryStep.StepCode,
                    ExecutionEpoch = DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                    DefinitionRevision = preflight.FlowPin.DefinitionRevision,
                    StepOrder = preflight.EntryStep.StepOrder,
                    FormNodeId = entryForm.FormNodeId,
                    FormFamilyId = entryForm.DynamicFormFamilyId,
                    FormVersionId = entryForm.DynamicFormTemplateId,
                    FormVersionNo = entryForm.DynamicFormVersionNo,
                    FormSchemaHash = entryForm.DynamicFormSchemaHash,
                    FormSnapshotHash = entryForm.DynamicFormSnapshotHash,
                    TargetUnitId = target.TargetUnitId,
                    ParticipantUserIds = target.AssigneeUserIds.ToList(),
                    ParticipantSnapshotId = snapshotId,
                    AttemptNo = 1,
                    ReviewCycleNo =
                        DynamicFlowReviewLoopTopologyContract.InitialReviewCycleNo,
                    BranchId = branchId,
                    NextNodeIds = preflight.EntryStep.NextStepIds.ToList(),
                    IsTerminalNode = preflight.EntryStep.IsTerminalNode,
                    IsCanonicalEpoch = true,
                    ResultOwnerIdentity = reviewLoopTopology is not null
                        ? DynamicFlowReviewLoopTopologyContract.BuildOwnerIdentity(
                            instanceId,
                            DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                            preflight.EntryStep.StepId,
                            branchId,
                            1,
                            "result")
                        : finalizeTopology is not null
                        ? DynamicFlowFinalizeTopologyContract.BuildOwnerIdentity(
                            instanceId,
                            DynamicFlowSequentialTopologyContract
                                .InitialExecutionEpoch,
                            preflight.EntryStep.StepId,
                            branchId,
                            "result")
                        : sequentialTopology is null && parallelForkTopology is null
                        ? BuildTopologyOwnerIdentity(
                            instanceId,
                            DynamicFlowSequentialTopologyContract
                                .InitialExecutionEpoch,
                            preflight.EntryStep.StepId,
                            "result")
                        : DynamicFlowParallelForkTopologyContract
                            .BuildStepOwnerIdentity(
                                instanceId,
                                DynamicFlowSequentialTopologyContract
                                    .InitialExecutionEpoch,
                                sequentialTopology?.Payload.ResultOwnerStepId ??
                                parallelForkTopology?.Payload.ResultOwnerStepId ??
                                preflight.EntryStep.StepId,
                                branchId,
                                "result"),
                    StatisticOwnerIdentity = reviewLoopTopology is not null
                        ? DynamicFlowReviewLoopTopologyContract.BuildOwnerIdentity(
                            instanceId,
                            DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                            preflight.EntryStep.StepId,
                            branchId,
                            1,
                            "statistics")
                        : finalizeTopology is not null
                        ? DynamicFlowFinalizeTopologyContract.BuildOwnerIdentity(
                            instanceId,
                            DynamicFlowSequentialTopologyContract
                                .InitialExecutionEpoch,
                            preflight.EntryStep.StepId,
                            branchId,
                            "statistics")
                        : sequentialTopology is null && parallelForkTopology is null
                        ? BuildTopologyOwnerIdentity(
                            instanceId,
                            DynamicFlowSequentialTopologyContract
                                .InitialExecutionEpoch,
                            preflight.EntryStep.StepId,
                            "statistics")
                        : DynamicFlowParallelForkTopologyContract
                            .BuildStepOwnerIdentity(
                                instanceId,
                                DynamicFlowSequentialTopologyContract
                                    .InitialExecutionEpoch,
                                sequentialTopology?.Payload.StatisticsOwnerStepId ??
                                parallelForkTopology?.Payload.StatisticsOwnerStepId ??
                                preflight.EntryStep.StepId,
                                branchId,
                                "statistics"),
                    AssignmentId = assignmentId,
                    State = DynamicFlowStepStates.Materializing,
                    Revision = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = actorUserId,
                    UpdatedByUserId = actorUserId,
                    IsDeleted = false
                };
                var bindingClaims = target.Participants
                    .OrderBy(user => user.UserId, StringComparer.Ordinal)
                    .Select(user => new WorkTemplateAssignee
                    {
                        Id = StableObjectId($"{assignmentId}\nbinding\n{user.UserId}"),
                        WorkId = preflight.WorkId,
                        WorkAssignmentId = assignmentId,
                        DynamicFormTemplateId = entryForm.DynamicFormTemplateId,
                        DynamicFormFamilyId = entryForm.DynamicFormFamilyId,
                        DynamicFormVersionNo = entryForm.DynamicFormVersionNo,
                        DynamicFormSchemaHash = entryForm.DynamicFormSchemaHash,
                        AssigneeUserId = user.UserId,
                        AssigneeUsername = user.Username,
                        AssigneeFullName = user.FullName,
                        AssigneeUnitId = user.UnitId,
                        AssigneeUnitSymbol = user.UnitSymbol,
                        AssigneeUnitShortName = user.UnitShortName,
                        AssigneeUnitName = user.UnitName,
                        AssignmentType = WorkAssignmentTypes.Once,
                        AggregationType = WorkAggregationTypes.Matrix,
                        IsActive = true,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = actorUserId,
                        UpdatedByUserId = actorUserId,
                        IsDeleted = false
                    })
                    .ToList();
                var outboxPayload = new BsonDocument
                {
                    { "commandId", preflight.CommandId },
                    { "actorUserId", actorUserId },
                    { "issuerUnitId", preflight.IssuerUnitId },
                    { "archetypeId", preflight.FlowPin.ArchetypeId },
                    { "branchOrdinal", ordinal },
                    { "targetUnitId", target.TargetUnitId },
                    { "assignmentId", assignmentId },
                    { "periodKey", preflight.PeriodKey },
                    { "scheduleIdentityJson", preflight.ScheduleIdentityJson },
                    { "scheduleIdentityHash", preflight.ScheduleIdentityHash },
                    {
                        "periodicScheduleId",
                        BsonValue.Create(periodicContext?.ScheduleId)
                    },
                    {
                        "periodicOccurrenceId",
                        BsonValue.Create(periodicContext?.OccurrenceId)
                    },
                    {
                        "timeZoneId",
                        BsonValue.Create(periodicContext?.TimeZoneId)
                    },
                    {
                        "schedulePolicyVersion",
                        BsonValue.Create(periodicContext?.PolicyVersion)
                    },
                    { "executionEpoch", step.ExecutionEpoch },
                    { "branchId", step.BranchId },
                    { "attemptNo", step.AttemptNo },
                    { "reviewCycleNo", step.ReviewCycleNo },
                    {
                        "participants",
                        new BsonArray(target.Participants
                            .OrderBy(user => user.UserId, StringComparer.Ordinal)
                            .Select(ParticipantDocument))
                    }
                };
                return new RuntimeIntentBranch(
                    ordinal,
                    step,
                    bindingClaims,
                    new DynamicFlowRuntimeOutboxItem
                    {
                        Id = StableObjectId($"{stepId}\noutbox\nmaterialize"),
                        FlowInstanceId = instanceId,
                        StepInstanceId = stepId,
                        Operation = DynamicFlowRuntimeMaterializationOperations.MaterializeEntryAssignment,
                        DedupeKey = finalizeTopology is not null
                            ? $"{instanceId}:1:{preflight.EntryStep.StepId}:{target.TargetUnitId}:materialize"
                            : $"{instanceId}:{preflight.EntryStep.StepId}:{target.TargetUnitId}:1:materialize",
                        Payload = outboxPayload,
                        PayloadHash = Hash(outboxPayload.ToJson()),
                        Status = DynamicFlowRuntimeOutboxStatuses.Pending,
                        AttemptCount = 0,
                        RepairEpoch = 0,
                        NextAttemptAtUtc = now,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
            })
            .ToList();
        var issuerUnitId = preflight.IssuerUnitId;
        if (!ObjectId.TryParse(issuerUnitId, out _))
            throw new InvalidOperationException("DYNAMIC_FLOW_ISSUER_UNIT_REQUIRED");
        var snapshot = new DynamicFlowParticipantSnapshot
        {
            Id = snapshotId,
            FlowInstanceId = instanceId,
            IssuerUserId = actorUserId,
            IssuerUnitId = issuerUnitId,
            Bindings = preflight.Targets
                .Select(target => new DynamicFlowParticipantBinding
                {
                    TargetUnitId = target.TargetUnitId,
                    AssigneeUserIds = target.AssigneeUserIds.ToList(),
                    Participants = target.Participants
                        .OrderBy(user => user.UserId, StringComparer.Ordinal)
                        .Select(user => new DynamicFlowParticipantUserSnapshot
                        {
                            UserId = user.UserId,
                            Username = user.Username,
                            FullName = user.FullName,
                            UnitId = user.UnitId,
                            UnitSymbol = user.UnitSymbol,
                            UnitShortName = user.UnitShortName,
                            UnitName = user.UnitName,
                            PositionCode = user.PositionCode,
                            PositionName = user.PositionName
                        })
                        .ToList(),
                    RoleCodes = new List<string> { DynamicFlowRuntimePlanner.AssignmentFlowRole }
                })
                .ToList(),
            SourceRevisionTokens = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["catalog"] = preflight.FlowPin.CatalogSemanticHash,
                ["flow"] = preflight.FlowPin.PayloadHash,
                ["topology"] = preflight.FlowPin.TopologyHash,
                ["forms"] = Hash(string.Join(
                    "\n",
                    preflight.FormPins
                        .OrderBy(pin => pin.FormNodeId, StringComparer.Ordinal)
                        .Select(pin => $"{pin.FormNodeId}:{pin.DynamicFormSchemaHash}"))),
                ["schedule"] = preflight.ScheduleIdentityHash
            },
            SnapshotHash = string.Empty,
            FrozenAtUtc = now
        };
        snapshot.SnapshotHash = ParticipantSnapshotHash(snapshot);
        var instance = new DynamicFlowInstance
        {
            Id = instanceId,
            WorkId = preflight.WorkId,
            WorkType = preflight.WorkType,
            FlowTemplateId = preflight.FlowPin.FlowTemplateId,
            FlowTemplateVersionId = preflight.FlowPin.FlowTemplateVersionId,
            FlowTemplateVersionNo = preflight.FlowPin.FlowTemplateVersionNo,
            FlowPayloadHash = preflight.FlowPin.PayloadHash,
            CatalogVersion = preflight.FlowPin.CatalogVersion,
            CatalogSemanticHash = preflight.FlowPin.CatalogSemanticHash,
            ArchetypeId = preflight.FlowPin.ArchetypeId,
            DefinitionRevision = preflight.FlowPin.DefinitionRevision,
            TopologySnapshotJson = preflight.LockedDefinitionJson,
            TopologySnapshotHash = preflight.FlowPin.TopologyHash,
            ExecutionEpoch = DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
            EntryFlowStepId = preflight.EntryStep.StepId,
            ParentInstanceId = subflowContext?.ParentInstanceId,
            ParentStepInstanceId = subflowContext?.ParentStepInstanceId,
            RootInstanceId = subflowContext?.RootInstanceId,
            AncestryPath = subflowContext?.AncestryPath.ToList() ?? new(),
            AncestryFlowFamilyIds =
                subflowContext?.AncestryFlowFamilyIds.ToList() ?? new(),
            StatisticOwnerIdentity = BuildTopologyOwnerIdentity(
                instanceId,
                DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
                sequentialTopology?.Payload.StatisticsOwnerStepId ??
                parallelForkTopology?.Payload.StatisticsOwnerStepId ??
                preflight.EntryStep.StepId,
                "statistics"),
            PeriodKey = preflight.PeriodKey,
            ScheduleIdentityJson = preflight.ScheduleIdentityJson,
            ScheduleIdentityHash = preflight.ScheduleIdentityHash,
            PeriodicScheduleId = periodicContext?.ScheduleId,
            PeriodicOccurrenceId = periodicContext?.OccurrenceId,
            TimeZoneId = periodicContext?.TimeZoneId,
            SchedulePolicyVersion = periodicContext?.PolicyVersion,
            ParticipantSnapshotId = snapshotId,
            ParticipantSnapshotHash = snapshot.SnapshotHash,
            IssuerUserId = actorUserId,
            IssuerUnitId = issuerUnitId,
            LaunchCommandId = preflight.CommandId,
            State = DynamicFlowInstanceStates.Materializing,
            Revision = 1,
            NextEventSequence = branches.Count + 2,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };
        var initialPayload = new BsonDocument
        {
            { "requestHash", preflight.RequestHash },
            { "snapshotToken", preflight.SnapshotToken },
            { "branchCount", branches.Count },
            { "periodKey", preflight.PeriodKey },
            {
                "periodicScheduleId",
                BsonValue.Create(periodicContext?.ScheduleId)
            },
            {
                "periodicOccurrenceId",
                BsonValue.Create(periodicContext?.OccurrenceId)
            },
            {
                "timeZoneId",
                BsonValue.Create(periodicContext?.TimeZoneId)
            },
            {
                "schedulePolicyVersion",
                BsonValue.Create(periodicContext?.PolicyVersion)
            },
            { "scheduleIdentityHash", preflight.ScheduleIdentityHash }
        };
        var initialResult = new BsonDocument
        {
            { "flowInstanceId", instanceId },
            { "status", "MATERIALIZING" },
            { "instanceState", DynamicFlowInstanceStates.Materializing },
            { "stepInstanceIds", new BsonArray(branches.Select(branch => branch.Step.Id)) },
            { "assignmentIds", new BsonArray(branches.Select(branch => branch.Step.AssignmentId!)) }
        };
        var receipt = new DynamicFlowRuntimeCommandReceipt
        {
            Id = StableObjectId(
                $"{DynamicFlowCommandScopeKinds.Launch}\n{LaunchScopeId(preflight.WorkId, preflight.FlowPin.FlowTemplateVersionId)}\nLAUNCH\n{preflight.CommandId}"),
            ScopeKind = DynamicFlowCommandScopeKinds.Launch,
            ScopeId = LaunchScopeId(preflight.WorkId, preflight.FlowPin.FlowTemplateVersionId),
            WorkId = preflight.WorkId,
            FlowTemplateVersionId = preflight.FlowPin.FlowTemplateVersionId,
            FlowInstanceId = instanceId,
            CommandType = "LAUNCH",
            CommandId = preflight.CommandId,
            RequestHash = preflight.RequestHash,
            CommandIdentityHash = preflight.CommandIdentityHash,
            SnapshotToken = preflight.SnapshotToken,
            Status = DynamicFlowRuntimeCommandStatuses.Pending,
            ResultSnapshot = initialResult,
            ResultSnapshotHash = Hash(initialResult.ToJson()),
            CreatedAtUtc = now
        };
        return new RuntimeIntent(
            instance,
            snapshot,
            receipt,
            new DynamicFlowRuntimeEvent
            {
                Id = StableObjectId($"{instanceId}\nevent\nlaunch"),
                FlowInstanceId = instanceId,
                Sequence = 1,
                EventType = DynamicFlowRuntimeEventTypes.LaunchIntentCommitted,
                CommandId = preflight.CommandId,
                ActorUserId = actorUserId,
                VisibleUnitIds = preflight.Targets
                    .Select(target => target.TargetUnitId)
                    .Append(preflight.IssuerUnitId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                Payload = initialPayload,
                PayloadHash = Hash(initialPayload.ToJson()),
                OccurredAtUtc = now
            },
            branches);
    }

    private static string BuildTopologyOwnerIdentity(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string ownerKind)
        => $"{ownerKind}:{instanceId}:{executionEpoch}:{nodeId}";

    private static string LaunchScopeId(string workId, string versionId)
        => $"{workId}:{versionId}";

    private static List<UserRef> FrozenUsers(BsonArray participants)
        => participants
            .Select(value => value.AsBsonDocument)
            .Select(user => new UserRef
            {
                UserId = user["userId"].AsString,
                Username = OptionalString(user, "username"),
                FullName = OptionalString(user, "fullName"),
                UnitId = OptionalString(user, "unitId"),
                UnitSymbol = OptionalString(user, "unitSymbol"),
                UnitShortName = OptionalString(user, "unitShortName"),
                UnitName = OptionalString(user, "unitName"),
                PositionCode = OptionalString(user, "positionCode"),
                PositionName = OptionalString(user, "positionName")
            })
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToList();

    private static List<UserRef> FrozenUsers(
        IEnumerable<DynamicFlowParticipantUserSnapshot> participants)
        => participants
            .Select(user => new UserRef
            {
                UserId = user.UserId,
                Username = user.Username,
                FullName = user.FullName,
                UnitId = user.UnitId,
                UnitSymbol = user.UnitSymbol,
                UnitShortName = user.UnitShortName,
                UnitName = user.UnitName,
                PositionCode = user.PositionCode,
                PositionName = user.PositionName
            })
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToList();

    private static BsonDocument ParticipantDocument(
        DynamicFlowParticipantUserSnapshotDto user)
        => new()
        {
            { "userId", user.UserId },
            { "username", user.Username },
            { "fullName", user.FullName },
            { "unitId", user.UnitId },
            { "unitSymbol", BsonValue.Create(user.UnitSymbol) },
            { "unitShortName", BsonValue.Create(user.UnitShortName) },
            { "unitName", BsonValue.Create(user.UnitName) },
            { "positionCode", BsonValue.Create(user.PositionCode) },
            { "positionName", BsonValue.Create(user.PositionName) }
        };

    private static BsonDocument ParticipantDocument(
        DynamicFlowParticipantUserSnapshot user)
        => new()
        {
            { "userId", user.UserId },
            { "username", user.Username },
            { "fullName", user.FullName },
            { "unitId", user.UnitId },
            { "unitSymbol", BsonValue.Create(user.UnitSymbol) },
            { "unitShortName", BsonValue.Create(user.UnitShortName) },
            { "unitName", BsonValue.Create(user.UnitName) },
            { "positionCode", BsonValue.Create(user.PositionCode) },
            { "positionName", BsonValue.Create(user.PositionName) }
        };

    private static bool ValidateRuntimeReceipt(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        DynamicFlowRuntimeCommandReceipt receipt,
        IReadOnlyList<DynamicFlowRuntimeOutboxItem> outboxItems)
    {
        var entryProof = proof
            .Where(branch =>
                branch.Step.FlowStepId == instance.EntryFlowStepId)
            .OrderBy(branch => branch.Ordinal)
            .ToArray();
        var entryOutboxItems = outboxItems
            .Where(item =>
                entryProof.Any(branch =>
                    branch.Step.Id == item.StepInstanceId))
            .ToArray();
        if (entryProof.Length == 0 ||
            entryOutboxItems.Length != entryProof.Length)
            return false;
        if (receipt.Id != StableObjectId(
                $"{DynamicFlowCommandScopeKinds.Launch}\n{LaunchScopeId(instance.WorkId, instance.FlowTemplateVersionId)}\nLAUNCH\n{instance.LaunchCommandId}") ||
            receipt.ScopeKind != DynamicFlowCommandScopeKinds.Launch ||
            receipt.ScopeId != LaunchScopeId(instance.WorkId, instance.FlowTemplateVersionId) ||
            receipt.WorkId != instance.WorkId ||
            receipt.FlowTemplateVersionId != instance.FlowTemplateVersionId ||
            receipt.FlowInstanceId != instance.Id ||
            receipt.CommandType != "LAUNCH" ||
            receipt.CommandId != instance.LaunchCommandId ||
            !IsSha256(receipt.RequestHash) ||
            !IsSha256(receipt.CommandIdentityHash) ||
            !IsSha256(receipt.SnapshotToken) ||
            receipt.ResultSnapshot is null ||
            !FixedEquals(
                receipt.ResultSnapshotHash,
                Hash(receipt.ResultSnapshot.ToJson())) ||
            receipt.Status is not (
                DynamicFlowRuntimeCommandStatuses.Pending or
                DynamicFlowRuntimeCommandStatuses.Succeeded) ||
            !TryReadString(
                receipt.ResultSnapshot,
                "flowInstanceId",
                out var resultInstanceId) ||
            resultInstanceId != instance.Id)
        {
            return false;
        }

        var expectedSnapshotToken =
            Hash($"{receipt.RequestHash}\n{instance.FlowPayloadHash}\n{instance.ScheduleIdentityHash}");
        if (!FixedEquals(receipt.SnapshotToken, expectedSnapshotToken))
            return false;
        if (!TryReadString(receipt.ResultSnapshot, "status", out var resultStatus) ||
            (receipt.Status == DynamicFlowRuntimeCommandStatuses.Succeeded &&
             resultStatus != "SUCCEEDED") ||
            (receipt.Status == DynamicFlowRuntimeCommandStatuses.Pending &&
             resultStatus != "MATERIALIZING"))
        {
            return false;
        }

        var expectedStepIds = entryProof
            .Select(branch => branch.Step.Id)
            .ToArray();
        var expectedAssignmentIds = entryProof
            .Select(branch => branch.AssignmentId)
            .ToArray();
        if (!BsonStringArrayEquals(
                receipt.ResultSnapshot,
                "stepInstanceIds",
                expectedStepIds) ||
            !BsonStringArrayEquals(
                receipt.ResultSnapshot,
                "assignmentIds",
                expectedAssignmentIds))
        {
            return false;
        }

        var scheduleIdentityJson = entryOutboxItems
            .Select(item =>
                TryReadString(item.Payload, "scheduleIdentityJson", out var value)
                    ? value
                    : null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scheduleIdentityJson.Length != 1 ||
            scheduleIdentityJson[0] is null)
        {
            return false;
        }

        try
        {
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = instance.FlowTemplateVersionId,
                CommandId = instance.LaunchCommandId,
                TargetUnitIds = entryProof
                    .Select(branch => branch.Step.TargetUnitId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                PeriodKey = instance.PeriodKey,
                ScheduleIdentityJson = scheduleIdentityJson[0]
            };
            var commandIdentityHash =
                DynamicFlowRuntimePreflightContract.BuildCommandIdentityHash(
                    instance.WorkId,
                    instance.FlowTemplateVersionId,
                    request,
                    snapshot.IssuerUserId);
            return FixedEquals(receipt.CommandIdentityHash, commandIdentityHash);
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryBuildForwardReceiptIndex(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> receipts,
        out Dictionary<string, DynamicFlowRuntimeCommandReceipt> byNextStepId)
    {
        if (IsParallelFanOutArchetype(instance.ArchetypeId))
        {
            return TryBuildParallelForkReceiptIndex(
                instance,
                proof,
                receipts,
                out byNextStepId);
        }
        byNextStepId = new Dictionary<
            string,
            DynamicFlowRuntimeCommandReceipt>(StringComparer.Ordinal);
        var sequentialSteps = proof
            .Where(branch =>
                branch.Step.FlowStepId != instance.EntryFlowStepId)
            .ToArray();
        if (receipts.Count != sequentialSteps.Length ||
            receipts.Select(receipt => receipt.CommandId)
                .Distinct(StringComparer.Ordinal)
                .Count() != receipts.Count)
        {
            return false;
        }

        foreach (var receipt in receipts)
        {
            var result = receipt.ResultSnapshot;
            if (result is null ||
                receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
                receipt.ScopeId != instance.Id ||
                receipt.WorkId != instance.WorkId ||
                receipt.FlowTemplateVersionId != instance.FlowTemplateVersionId ||
                receipt.FlowInstanceId != instance.Id ||
                receipt.CommandType != "FORWARD" ||
                string.IsNullOrWhiteSpace(receipt.CommandId) ||
                receipt.Id !=
                DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(
                    instance.Id,
                    receipt.CommandId) ||
                receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
                receipt.ExpectedRevision is null or < 1 ||
                string.IsNullOrWhiteSpace(receipt.UpdatedByUserId) ||
                !ObjectId.TryParse(receipt.UpdatedByUserId, out _) ||
                receipt.CompletedAtUtc is null ||
                !IsSha256(receipt.RequestHash) ||
                !FixedEquals(
                    receipt.CommandIdentityHash,
                    Hash(
                        $"{receipt.UpdatedByUserId}\n{instance.Id}\n{receipt.CommandId}\nFORWARD")) ||
                !FixedEquals(
                    receipt.SnapshotToken,
                    Hash(
                        $"{receipt.RequestHash}\n{instance.TopologySnapshotHash}")) ||
                !FixedEquals(
                    receipt.ResultSnapshotHash,
                    Hash(result.ToJson())) ||
                !TryReadString(result, "commandId", out var resultCommandId) ||
                resultCommandId != receipt.CommandId ||
                !TryReadString(result, "status", out var status) ||
                status != "ACCEPTED_PENDING_MATERIALIZATION" ||
                !result.TryGetValue(
                    "businessWritePerformed",
                    out var businessWriteValue) ||
                !businessWriteValue.IsBoolean ||
                !businessWriteValue.AsBoolean ||
                !TryReadString(
                    result,
                    "flowInstanceId",
                    out var flowInstanceId) ||
                flowInstanceId != instance.Id ||
                !TryReadInt32(
                    result,
                    "executionEpoch",
                    out var executionEpoch) ||
                executionEpoch != instance.ExecutionEpoch ||
                !TryReadInt64(
                    result,
                    "instanceRevision",
                    out var instanceRevision) ||
                instanceRevision != receipt.ExpectedRevision.Value + 1 ||
                !TryReadString(
                    result,
                    "stepInstanceId",
                    out var parentStepId) ||
                !TryReadString(
                    result,
                    "nextStepInstanceId",
                    out var nextStepId) ||
                !TryReadString(
                    result,
                    "nextAssignmentId",
                    out var nextAssignmentId) ||
                !TryReadString(
                    result,
                    "activatedTransitionId",
                    out var transitionId) ||
                !TryReadString(result, "eventId", out var eventId) ||
                eventId != StableObjectId(
                    $"{instance.Id}\n{instance.ExecutionEpoch}\nforward\n{receipt.CommandId}\naccepted"))
            {
                return false;
            }

            var next = sequentialSteps.SingleOrDefault(branch =>
                branch.Step.Id == nextStepId &&
                branch.AssignmentId == nextAssignmentId &&
                branch.Step.ActivatedByTransitionId == transitionId);
            var parent = next is null
                ? null
                : proof.SingleOrDefault(branch =>
                    branch.Step.Id == parentStepId &&
                    branch.Step.TargetUnitId == next.Step.TargetUnitId &&
                    branch.BranchId == next.BranchId &&
                    branch.Step.StepOrder + 1 == next.Step.StepOrder);
            if (next is null ||
                parent is null ||
                receipt.UpdatedByUserId != instance.IssuerUserId &&
                !next.Binding.AssigneeUserIds.Contains(
                    receipt.UpdatedByUserId,
                    StringComparer.Ordinal) ||
                !byNextStepId.TryAdd(nextStepId, receipt))
            {
                return false;
            }
        }

        return byNextStepId.Count == sequentialSteps.Length;
    }

    private static bool TryBuildParallelForkReceiptIndex(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> receipts,
        out Dictionary<string, DynamicFlowRuntimeCommandReceipt> byNextStepId)
    {
        byNextStepId = new Dictionary<string, DynamicFlowRuntimeCommandReceipt>(
            StringComparer.Ordinal);
        var branchSteps = proof
            .Where(branch => branch.Step.FlowStepId != instance.EntryFlowStepId)
            .ToArray();
        var branchesPerReceipt =
            instance.ArchetypeId ==
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId
                ? 1
                : 2;
        if (branchSteps.Length % branchesPerReceipt != 0 ||
            receipts.Count != branchSteps.Length / branchesPerReceipt ||
            receipts.Select(receipt => receipt.CommandId)
                .Distinct(StringComparer.Ordinal)
                .Count() != receipts.Count)
        {
            return false;
        }

        foreach (var receipt in receipts)
        {
            var result = receipt.ResultSnapshot;
            if (result is null ||
                receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
                receipt.ScopeId != instance.Id ||
                receipt.WorkId != instance.WorkId ||
                receipt.FlowTemplateVersionId != instance.FlowTemplateVersionId ||
                receipt.FlowInstanceId != instance.Id ||
                receipt.CommandType != "FORWARD" ||
                receipt.Id !=
                DynamicFlowParallelForkTopologyContract.BuildForwardReceiptId(
                    instance.Id,
                    receipt.CommandId) ||
                receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
                receipt.ExpectedRevision is null or < 1 ||
                string.IsNullOrWhiteSpace(receipt.UpdatedByUserId) ||
                receipt.CompletedAtUtc is null ||
                !FixedEquals(receipt.ResultSnapshotHash, Hash(result.ToJson())) ||
                !TryReadString(result, "flowInstanceId", out var flowInstanceId) ||
                flowInstanceId != instance.Id ||
                !TryReadString(result, "stepInstanceId", out var parentStepId) ||
                !result.TryGetValue("activatedBranches", out var activated) ||
                !activated.IsBsonArray ||
                activated.AsBsonArray.Count != branchesPerReceipt ||
                !TryReadString(
                    result,
                    "gatewayInstanceId",
                    out var gatewayInstanceId) ||
                !TryReadString(result, "eventId", out var eventId) ||
                eventId != StableObjectId(
                    $"{instance.Id}\n{instance.ExecutionEpoch}\nfork\n{receipt.CommandId}\naccepted"))
            {
                return false;
            }

            var parent = proof.SingleOrDefault(branch =>
                branch.Step.Id == parentStepId &&
                branch.Step.FlowStepId == instance.EntryFlowStepId);
            if (parent is null)
                return false;
            foreach (var value in activated.AsBsonArray)
            {
                var branchResult = value.AsBsonDocument;
                if (!TryReadString(
                        branchResult,
                        "stepInstanceId",
                        out var stepId) ||
                    !TryReadString(
                        branchResult,
                        "assignmentId",
                        out var assignmentId) ||
                    !TryReadString(
                        branchResult,
                        "branchId",
                        out var branchId) ||
                    !TryReadString(
                        branchResult,
                        "parentBranchId",
                        out var parentBranchId) ||
                    !TryReadString(
                        branchResult,
                        "gatewayInstanceId",
                        out var branchGatewayId) ||
                    !TryReadString(
                        branchResult,
                        "contributionId",
                        out var contributionId) ||
                    !TryReadString(
                        branchResult,
                        "transitionId",
                        out var transitionId))
                {
                    return false;
                }
                var next = branchSteps.SingleOrDefault(branch =>
                    branch.Step.Id == stepId &&
                    branch.AssignmentId == assignmentId &&
                    branch.BranchId == branchId &&
                    branch.Step.TargetUnitId == parent.Step.TargetUnitId &&
                    branch.Step.GatewayInstanceId == gatewayInstanceId &&
                    branch.Step.GatewayInstanceId == branchGatewayId &&
                    branch.Step.ContributionId == contributionId &&
                    branch.Step.ActivatedByTransitionId == transitionId);
                if (next is null ||
                    parentBranchId != parent.BranchId ||
                    !byNextStepId.TryAdd(stepId, receipt))
                {
                    return false;
                }
            }
        }
        return byNextStepId.Count == branchSteps.Length;
    }

    private static bool ValidateRuntimeOutbox(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeOutboxItem> outboxItems,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> forwardReceipts)
    {
        if (IsParallelFanOutArchetype(instance.ArchetypeId))
        {
            return ValidateParallelForkOutbox(
                instance,
                snapshot,
                proof,
                outboxItems,
                forwardReceipts);
        }
        if (outboxItems.Count != proof.Count ||
            outboxItems.GroupBy(item => item.StepInstanceId, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            !TryBuildForwardReceiptIndex(
                instance,
                proof,
                forwardReceipts,
                out var forwardReceiptByStepId))
        {
            return false;
        }

        foreach (var branch in proof)
        {
            var item = outboxItems.SingleOrDefault(candidate =>
                candidate.StepInstanceId == branch.Step.Id);
            var isEntry =
                branch.Step.FlowStepId == instance.EntryFlowStepId;
            forwardReceiptByStepId.TryGetValue(
                branch.Step.Id,
                out var forwardReceipt);
            if (item is null ||
                item.Id != StableObjectId($"{branch.Step.Id}\noutbox\nmaterialize") ||
                item.FlowInstanceId != instance.Id ||
                item.StepInstanceId != branch.Step.Id ||
                item.AttemptCount < 0 ||
                item.RepairEpoch < 0 ||
                !FixedEquals(item.PayloadHash, Hash(item.Payload.ToJson())) ||
                !TryReadString(item.Payload, "commandId", out var commandId) ||
                !TryReadString(item.Payload, "actorUserId", out var actorUserId) ||
                !TryReadString(item.Payload, "issuerUnitId", out var issuerUnitId) ||
                issuerUnitId != snapshot.IssuerUnitId ||
                !TryReadString(item.Payload, "archetypeId", out var archetypeId) ||
                archetypeId != instance.ArchetypeId ||
                !TryReadInt32(item.Payload, "branchOrdinal", out var ordinal) ||
                !TryReadString(item.Payload, "targetUnitId", out var targetUnitId) ||
                targetUnitId != branch.Step.TargetUnitId ||
                !TryReadString(item.Payload, "assignmentId", out var assignmentId) ||
                assignmentId != branch.AssignmentId ||
                !TryReadString(item.Payload, "periodKey", out var periodKey) ||
                periodKey != instance.PeriodKey ||
                !TryReadString(
                    item.Payload,
                    "scheduleIdentityHash",
                    out var scheduleIdentityHash) ||
                scheduleIdentityHash != instance.ScheduleIdentityHash ||
                !TryReadString(
                    item.Payload,
                    "scheduleIdentityJson",
                    out var scheduleIdentityJson) ||
                scheduleIdentityJson != instance.ScheduleIdentityJson ||
                !FixedEquals(
                    Hash(scheduleIdentityJson),
                    instance.ScheduleIdentityHash) ||
                !item.Payload.TryGetValue("participants", out var participants) ||
                !participants.IsBsonArray ||
                participants.AsBsonArray.ToJson() !=
                new BsonArray(branch.Binding.Participants
                    .OrderBy(user => user.UserId, StringComparer.Ordinal)
                    .Select(ParticipantDocument))
                .ToJson())
            {
                return false;
            }

            if (isEntry)
            {
                var expectedEntryDedupeKey =
                    instance.ArchetypeId ==
                    DynamicFlowFinalizeTopologyContract.ArchetypeId
                        ? $"{instance.Id}:{instance.ExecutionEpoch}:" +
                          $"{branch.Step.FlowStepId}:" +
                          $"{branch.Step.TargetUnitId}:materialize"
                        : $"{instance.Id}:{branch.Step.FlowStepId}:" +
                          $"{branch.Step.TargetUnitId}:" +
                          $"{branch.Step.AttemptNo}:materialize";
                if (forwardReceipt is not null ||
                    item.Operation !=
                    DynamicFlowRuntimeMaterializationOperations
                        .MaterializeEntryAssignment ||
                    item.DedupeKey != expectedEntryDedupeKey ||
                    commandId != instance.LaunchCommandId ||
                    actorUserId != snapshot.IssuerUserId ||
                    ordinal != branch.Ordinal ||
                    (instance.ArchetypeId ==
                         DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                     (!TryReadInt32(
                          item.Payload,
                          "executionEpoch",
                          out var entryExecutionEpoch) ||
                      entryExecutionEpoch != instance.ExecutionEpoch)))
                {
                    return false;
                }
                continue;
            }

            if (forwardReceipt is null ||
                item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeSequentialAssignment ||
                item.DedupeKey !=
                $"{instance.Id}:{instance.ExecutionEpoch}:{branch.Step.FlowStepId}:{branch.BranchId}:{branch.Step.AttemptNo}:materialize" ||
                commandId != forwardReceipt.CommandId ||
                actorUserId != forwardReceipt.UpdatedByUserId ||
                ordinal != branch.Step.StepOrder ||
                !TryReadInt32(
                    item.Payload,
                    "executionEpoch",
                    out var executionEpoch) ||
                executionEpoch != instance.ExecutionEpoch ||
                !TryReadString(item.Payload, "branchId", out var branchId) ||
                branchId != branch.BranchId ||
                !TryReadInt32(item.Payload, "attemptNo", out var attemptNo) ||
                attemptNo != branch.Step.AttemptNo ||
                !TryReadString(
                    item.Payload,
                    "transitionId",
                    out var transitionId) ||
                transitionId != branch.Step.ActivatedByTransitionId ||
                !TryReadInt64(
                    item.Payload,
                    "materializationEventSequence",
                    out var materializationEventSequence) ||
                materializationEventSequence <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateParallelForkOutbox(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeOutboxItem> outboxItems,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> forwardReceipts)
    {
        if (outboxItems.Count != proof.Count ||
            !TryBuildParallelForkReceiptIndex(
                instance,
                proof,
                forwardReceipts,
                out var receiptByStepId))
        {
            return false;
        }
        foreach (var branch in proof)
        {
            var item = outboxItems.SingleOrDefault(candidate =>
                candidate.StepInstanceId == branch.Step.Id);
            var isEntry = branch.Step.FlowStepId == instance.EntryFlowStepId;
            receiptByStepId.TryGetValue(branch.Step.Id, out var receipt);
            if (item is null ||
                item.Id != StableObjectId($"{branch.Step.Id}\noutbox\nmaterialize") ||
                item.FlowInstanceId != instance.Id ||
                !FixedEquals(item.PayloadHash, Hash(item.Payload.ToJson())) ||
                !TryReadString(item.Payload, "assignmentId", out var assignmentId) ||
                assignmentId != branch.AssignmentId ||
                !TryReadString(item.Payload, "targetUnitId", out var targetUnitId) ||
                targetUnitId != branch.Step.TargetUnitId ||
                !TryReadString(item.Payload, "actorUserId", out var actorUserId) ||
                !item.Payload.TryGetValue("participants", out var participants) ||
                participants.AsBsonArray.ToJson() !=
                new BsonArray(branch.Binding.Participants
                    .OrderBy(user => user.UserId, StringComparer.Ordinal)
                    .Select(ParticipantDocument)).ToJson())
            {
                return false;
            }
            if (isEntry)
            {
                if (receipt is not null ||
                    item.Operation !=
                    DynamicFlowRuntimeMaterializationOperations
                        .MaterializeEntryAssignment ||
                    item.DedupeKey !=
                    $"{instance.Id}:{branch.Step.FlowStepId}:{branch.Step.TargetUnitId}:1:materialize" ||
                    actorUserId != snapshot.IssuerUserId)
                {
                    return false;
                }
                continue;
            }
            if (receipt is null ||
                item.Operation !=
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeForkBranchAssignment ||
                item.DedupeKey !=
                $"{instance.Id}:{instance.ExecutionEpoch}:{branch.Step.FlowStepId}:{branch.BranchId}:1:materialize" ||
                actorUserId != receipt.UpdatedByUserId ||
                !TryReadString(
                    item.Payload,
                    "gatewayInstanceId",
                    out var gatewayId) ||
                gatewayId != branch.Step.GatewayInstanceId ||
                !TryReadString(
                    item.Payload,
                    "contributionId",
                    out var contributionId) ||
                contributionId != branch.Step.ContributionId ||
                !TryReadString(
                    item.Payload,
                    "transitionId",
                    out var transitionId) ||
                transitionId != branch.Step.ActivatedByTransitionId)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidateRuntimeEvents(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        DynamicFlowRuntimeCommandReceipt launchReceipt,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> forwardReceipts,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> stateReceipts,
        IReadOnlyList<DynamicFlowRuntimeOutboxItem> outboxItems,
        IReadOnlyList<DynamicFlowRuntimeEvent> events,
        IReadOnlyList<WorkAssignmentReport> reports)
    {
        var orderedSequences = events
            .Select(item => item.Sequence)
            .OrderBy(value => value)
            .ToArray();
        if (events.Count == 0 ||
            instance.NextEventSequence != events.Count + 1L ||
            !orderedSequences.SequenceEqual(
                Enumerable.Range(1, events.Count).Select(value => (long)value)))
        {
            return false;
        }

        var stateEvents = events
            .Where(item =>
                DynamicFlowRuntimeStateProjectionEventTypes.All.Contains(item.EventType))
            .ToList();
        var baseEvents = events
            .Where(item =>
                !DynamicFlowRuntimeStateProjectionEventTypes.All.Contains(item.EventType))
            .ToList();
        return ValidateRuntimeBaseEvents(
                   instance,
                   proof,
                   launchReceipt,
                   forwardReceipts,
                   outboxItems,
                   baseEvents) &&
               ValidateRuntimeStateProjectionLedger(
                   instance,
                   proof,
                   stateReceipts,
                   stateEvents,
                   baseEvents,
                   reports);
    }

    private static bool ValidateRuntimeStateProjectionLedger(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> receipts,
        IReadOnlyList<DynamicFlowRuntimeEvent> events,
        IReadOnlyList<DynamicFlowRuntimeEvent> baseEvents,
        IReadOnlyList<WorkAssignmentReport> reports)
    {
        if (instance.ArchetypeId ==
            DynamicFlowSubflowTopologyContract.ArchetypeId)
        {
            return ValidateSubflowStateProjectionLedger(
                instance,
                proof,
                receipts,
                events,
                baseEvents);
        }
        if (instance.ArchetypeId ==
            DynamicFlowJoinAllTopologyContract.ArchetypeId)
        {
            return ValidateJoinAllStateProjectionLedger(
                instance,
                proof,
                receipts,
                events);
        }
        if (instance.ArchetypeId ==
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId)
        {
            return ValidateJoinQuorumStateProjectionLedger(
                instance,
                proof,
                receipts,
                events);
        }
        if (receipts.Count == 0)
        {
            var stableWithoutStateReceipts =
                IsParallelFanOutArchetype(instance.ArchetypeId)
                    ? proof.All(branch =>
                        branch.Step.FlowStepId == instance.EntryFlowStepId
                            ? branch.Step.State == DynamicFlowStepStates.Completed
                            : branch.Step.State == DynamicFlowStepStates.Assigned)
                    : proof.All(branch =>
                        branch.Step.State == DynamicFlowStepStates.Assigned);
            return events.Count == 0 &&
                   stableWithoutStateReceipts &&
               instance.State == DynamicFlowInstanceStates.Active;
    }

        var eventById = events
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var reportById = reports
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        var claimedEventIds = new HashSet<string>(StringComparer.Ordinal);
        var revisionChains = new List<(
            string StepInstanceId,
            long FirstSequence,
            long LastSequence,
            long FromStepRevision,
            long ToStepRevision,
            long FromInstanceRevision,
            long ToInstanceRevision)>();
        foreach (var receipt in receipts)
        {
            if (receipt.ResultSnapshot is null ||
                receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
                receipt.ScopeId != instance.Id ||
                receipt.WorkId != instance.WorkId ||
                receipt.FlowTemplateVersionId != instance.FlowTemplateVersionId ||
                receipt.FlowInstanceId != instance.Id ||
                receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
                receipt.CommandType is not (
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle or
                    DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete or
                    DynamicFlowRuntimeStateProjectionOperations.StateReconcile) ||
                !IsSha256(receipt.RequestHash) ||
                !IsSha256(receipt.CommandIdentityHash) ||
                !IsSha256(receipt.SnapshotToken) ||
                !FixedEquals(
                    receipt.ResultSnapshotHash,
                    Hash(receipt.ResultSnapshot.ToJson())) ||
                !TryReadString(receipt.ResultSnapshot, "flowInstanceId", out var flowInstanceId) ||
                flowInstanceId != instance.Id ||
                !TryReadString(receipt.ResultSnapshot, "stepInstanceId", out var stepInstanceId) ||
                !TryReadString(receipt.ResultSnapshot, "assignmentId", out var assignmentId) ||
                !TryReadString(receipt.ResultSnapshot, "commandType", out var commandType) ||
                commandType != receipt.CommandType ||
                !TryReadString(receipt.ResultSnapshot, "sourceCommandId", out var sourceCommandId) ||
                !TryReadString(receipt.ResultSnapshot, "sourceEventKey", out var sourceEventKey) ||
                sourceEventKey != receipt.CommandId ||
                !TryReadString(receipt.ResultSnapshot, "fromState", out var fromState) ||
                !TryReadString(receipt.ResultSnapshot, "toState", out var toState) ||
                !TryReadString(
                    receipt.ResultSnapshot,
                    "projectionFingerprint",
                    out var projectionFingerprint) ||
                !IsSha256(projectionFingerprint) ||
                !TryReadInt64(
                    receipt.ResultSnapshot,
                    "fromStepRevision",
                    out var fromStepRevision) ||
                !TryReadInt64(
                    receipt.ResultSnapshot,
                    "toStepRevision",
                    out var toStepRevision) ||
                !TryReadInt64(
                    receipt.ResultSnapshot,
                    "fromInstanceRevision",
                    out var fromInstanceRevision) ||
                !TryReadInt64(
                    receipt.ResultSnapshot,
                    "toInstanceRevision",
                    out var toInstanceRevision) ||
                receipt.ExpectedRevision != fromStepRevision ||
                receipt.Id != StableObjectId(
                    $"{DynamicFlowCommandScopeKinds.Instance}\n{instance.Id}\n{receipt.CommandType}\n{receipt.CommandId}") ||
                !FixedEquals(
                    receipt.CommandIdentityHash,
                    Hash(
                        $"{receipt.UpdatedByUserId}\n{assignmentId}\n{sourceCommandId}\n{sourceEventKey}")) ||
                !FixedEquals(
                    receipt.SnapshotToken,
                    Hash(
                        $"{receipt.RequestHash}\n{projectionFingerprint}\n{instance.FlowPayloadHash}")))
            {
                return false;
            }

            var branch = proof.SingleOrDefault(item =>
                item.Step.Id == stepInstanceId &&
                item.AssignmentId == assignmentId);
            if (branch is null ||
                receipt.UpdatedByUserId is null ||
                !ObjectId.TryParse(receipt.UpdatedByUserId, out _) ||
                !receipt.ResultSnapshot.TryGetValue("eventIds", out var eventIdsValue) ||
                !eventIdsValue.IsBsonArray)
            {
                return false;
            }
            var eventIds = eventIdsValue.AsBsonArray
                .Where(value => value.IsString)
                .Select(value => value.AsString)
                .ToArray();
            if (eventIds.Length != eventIdsValue.AsBsonArray.Count ||
                eventIds.Length == 0 ||
                eventIds.Distinct(StringComparer.Ordinal).Count() != eventIds.Length)
            {
                return false;
            }

            var receiptEvents = new List<DynamicFlowRuntimeEvent>(eventIds.Length);
            foreach (var eventId in eventIds)
            {
                if (!claimedEventIds.Add(eventId) ||
                    !eventById.TryGetValue(eventId, out var matches) ||
                    matches.Count != 1)
                {
                    return false;
                }
                receiptEvents.Add(matches[0]);
            }
            receiptEvents = receiptEvents.OrderBy(item => item.Sequence).ToList();
            var stepEvents = receiptEvents
                .Where(item => item.StepInstanceId == stepInstanceId)
                .ToList();
            var instanceEvents = receiptEvents
                .Where(item => item.StepInstanceId is null)
                .ToList();
            if (stepEvents.Count == 0 ||
                stepEvents[0].FromState != fromState ||
                stepEvents[^1].ToState != toState ||
                stepEvents[0].FromRevision != fromStepRevision ||
                stepEvents[^1].ToRevision != toStepRevision ||
                toStepRevision != fromStepRevision + stepEvents.Count ||
                toInstanceRevision != fromInstanceRevision + 1)
            {
                return false;
            }

            var expectedVisibleUnits = new[]
                {
                    branch.Step.TargetUnitId,
                    instance.IssuerUnitId
                }
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var expectedReason = receipt.CommandType switch
            {
                DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete =>
                    "ASSIGNMENT_COMPLETED",
                DynamicFlowRuntimeStateProjectionOperations.StateReconcile =>
                    "STATE_RECONCILE",
                _ => "REPORT_LIFECYCLE_"
            };
            var isReportLifecycle =
                receipt.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle;
            var isStateReconcile =
                receipt.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.StateReconcile;
            string? receiptSourceReportId = null;
            for (var index = 0; index < stepEvents.Count; index++)
            {
                var item = stepEvents[index];
                var expectedEventType = item.FromState == item.ToState
                    ? receipt.CommandType switch
                    {
                        DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete =>
                            DynamicFlowRuntimeStateProjectionEventTypes.AssignmentCompletionProjected,
                        DynamicFlowRuntimeStateProjectionOperations.StateReconcile =>
                            DynamicFlowRuntimeStateProjectionEventTypes.StateReconciled,
                        _ => DynamicFlowRuntimeStateProjectionEventTypes.ReportLifecycleProjected
                    }
                    : DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged;
                var reasonMatches = receipt.CommandType ==
                                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle
                    ? item.ReasonCode?.StartsWith(
                        expectedReason,
                        StringComparison.Ordinal) == true
                    : item.ReasonCode == expectedReason ||
                      (receipt.CommandType ==
                       DynamicFlowRuntimeStateProjectionOperations.StateReconcile &&
                       item.ReasonCode == "STATE_RECONCILE_INSTANCE_COMPLETION");
                var reportRefMatches = false;
                string? sourceReportId = null;
                WorkReportLifecycleProjectionOutboxEntry? sourceLifecycleEntry = null;
                var hasSourceReportId =
                    TryReadString(
                        item.Payload,
                        "sourceReportId",
                        out var parsedReportId);
                var sourceReportIsNull =
                    item.Payload.TryGetValue(
                        "sourceReportId",
                        out var sourceReportValue) &&
                    sourceReportValue.IsBsonNull;
                if (hasSourceReportId &&
                    reportById.TryGetValue(parsedReportId, out var sourceReport) &&
                    sourceReport.WorkAssignmentId == assignmentId)
                {
                    sourceReportId = parsedReportId;
                    if (isReportLifecycle)
                    {
                        var matchingEntries = (sourceReport.LifecycleProjectionOutbox ??
                                               new List<WorkReportLifecycleProjectionOutboxEntry>())
                            .Where(entry =>
                                Hash($"{sourceReport.Id}\n{entry.EntryKey}") ==
                                sourceEventKey)
                            .ToArray();
                        if (matchingEntries.Length == 1)
                            sourceLifecycleEntry = matchingEntries[0];
                        reportRefMatches =
                            sourceLifecycleEntry is not null &&
                            sourceLifecycleEntry.CommandId == sourceCommandId &&
                            item.AffectedRefs.Contains($"report:{parsedReportId}");
                    }
                    else
                    {
                        reportRefMatches =
                            isStateReconcile &&
                            item.AffectedRefs.Contains($"report:{parsedReportId}");
                    }
                }
                else if (isStateReconcile)
                {
                    reportRefMatches = sourceReportIsNull;
                }
                else if (!isReportLifecycle)
                {
                    reportRefMatches = sourceReportIsNull;
                }
                if (sourceReportId is not null)
                {
                    if (receiptSourceReportId is not null &&
                        receiptSourceReportId != sourceReportId)
                    {
                        return false;
                    }
                    receiptSourceReportId = sourceReportId;
                }
                var lifecyclePayloadMatches =
                    !isReportLifecycle ||
                    sourceLifecycleEntry is not null &&
                    TryReadInt32(
                        item.Payload,
                        "sourceLifecycleRevision",
                        out var sourceLifecycleRevision) &&
                    sourceLifecycleRevision == sourceLifecycleEntry.LifecycleRevision &&
                    TryReadString(
                        item.Payload,
                        "sourceLifecycleStatus",
                        out var sourceLifecycleStatus) &&
                    sourceLifecycleStatus ==
                    (sourceLifecycleEntry.ToStatus ?? string.Empty)
                        .Trim()
                        .ToUpperInvariant() &&
                    item.Payload.TryGetValue(
                        "sourceLifecycleIsActive",
                        out var sourceLifecycleActiveValue) &&
                    sourceLifecycleActiveValue.IsBoolean &&
                    sourceLifecycleActiveValue.AsBoolean ==
                    sourceLifecycleEntry.ToIsActive;
                var expectedAffectedRefs = new[]
                    {
                        $"assignment:{assignmentId}",
                        $"step:{stepInstanceId}"
                    }
                    .Append(sourceReportId is null ? null : $"report:{sourceReportId}")
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var payloadMatches =
                    TryReadString(
                        item.Payload,
                        "assignmentId",
                        out var payloadAssignmentId) &&
                    payloadAssignmentId == assignmentId &&
                    TryReadInt32(
                        item.Payload,
                        "executionEpoch",
                        out var payloadExecutionEpoch) &&
                    payloadExecutionEpoch == branch.Step.ExecutionEpoch &&
                    TryReadString(
                        item.Payload,
                        "definitionRevision",
                        out var payloadDefinitionRevision) &&
                    payloadDefinitionRevision ==
                    branch.Step.DefinitionRevision &&
                    TryReadString(
                        item.Payload,
                        "flowStepId",
                        out var payloadFlowStepId) &&
                    payloadFlowStepId == branch.Step.FlowStepId &&
                    TryReadString(
                        item.Payload,
                        "branchId",
                        out var payloadBranchId) &&
                    payloadBranchId == branch.BranchId &&
                    TryReadInt32(
                        item.Payload,
                        "attemptNo",
                        out var payloadAttemptNo) &&
                    payloadAttemptNo == branch.Step.AttemptNo &&
                    TryReadString(
                        item.Payload,
                        "sourceEventKey",
                        out var payloadSourceEventKey) &&
                    payloadSourceEventKey == sourceEventKey &&
                    TryReadString(
                        item.Payload,
                        "sourceCommandId",
                        out var payloadSourceCommandId) &&
                    payloadSourceCommandId == sourceCommandId &&
                    TryReadString(
                        item.Payload,
                        "fromState",
                        out var payloadFromState) &&
                    payloadFromState == item.FromState &&
                    TryReadString(
                        item.Payload,
                        "toState",
                        out var payloadToState) &&
                    payloadToState == item.ToState &&
                    TryReadString(
                        item.Payload,
                        "reasonCode",
                        out var payloadReasonCode) &&
                    payloadReasonCode == item.ReasonCode &&
                    TryReadString(
                        item.Payload,
                        "projectionFingerprint",
                        out var payloadProjectionFingerprint) &&
                    payloadProjectionFingerprint == projectionFingerprint;
                if (item.FlowInstanceId != instance.Id ||
                    item.StepInstanceId != branch.Step.Id ||
                    item.ExecutionEpoch != instance.ExecutionEpoch ||
                    item.ExecutionEpoch != branch.Step.ExecutionEpoch ||
                    item.BranchId != branch.BranchId ||
                    item.AttemptNo != branch.Step.AttemptNo ||
                    item.Id != StableObjectId(
                        $"{instance.Id}\nstate-event\n{receipt.Id}\n{index}") ||
                    item.EventType != expectedEventType ||
                    item.CommandId != sourceEventKey ||
                    item.SourceEventKey != sourceEventKey ||
                    item.CorrelationId != sourceCommandId ||
                    item.ActorUserId != receipt.UpdatedByUserId ||
                    item.FromRevision != fromStepRevision + index ||
                    item.ToRevision != fromStepRevision + index + 1 ||
                    string.IsNullOrWhiteSpace(item.FromState) ||
                    string.IsNullOrWhiteSpace(item.ToState) ||
                    !reasonMatches ||
                    !reportRefMatches ||
                    !lifecyclePayloadMatches ||
                    !payloadMatches ||
                    !item.VisibleUnitIds
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(expectedVisibleUnits) ||
                    !item.AffectedRefs
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(expectedAffectedRefs) ||
                    !FixedEquals(item.PayloadHash, Hash(item.Payload.ToJson())) ||
                    (index > 0 && stepEvents[index - 1].ToState != item.FromState) ||
                    (item.FromState != item.ToState &&
                     !DynamicFlowRuntimeStateContract.CanTransitionStep(
                         item.FromState,
                         item.ToState)))
                {
                    return false;
                }
            }

            var instanceCompleted =
                receipt.ResultSnapshot.TryGetValue("instanceCompleted", out var completedValue) &&
                completedValue.IsBoolean &&
                completedValue.AsBoolean;
            var expectedInstanceAffectedRefs = new[]
                {
                    $"assignment:{assignmentId}",
                    $"step:{stepInstanceId}"
                }
                .Append(
                    receiptSourceReportId is null
                        ? null
                        : $"report:{receiptSourceReportId}")
                .Where(value => value is not null)
                .Select(value => value!)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var instanceReportPayloadMatches =
                receiptSourceReportId is null
                    ? instanceEvents.Count == 0 ||
                      instanceEvents[0].Payload.TryGetValue(
                          "sourceReportId",
                          out var instanceSourceReportValue) &&
                      instanceSourceReportValue.IsBsonNull
                    : instanceEvents.Count == 1 &&
                      TryReadString(
                          instanceEvents[0].Payload,
                          "sourceReportId",
                          out var instanceSourceReportId) &&
                      instanceSourceReportId == receiptSourceReportId;
            if (instanceCompleted)
            {
                if (instanceEvents.Count != 1 ||
                    instanceEvents[0].Id != StableObjectId(
                        $"{instance.Id}\nstate-event\n{receipt.Id}\ninstance-completed") ||
                    instanceEvents[0].EventType !=
                    DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted ||
                    instanceEvents[0].FlowInstanceId != instance.Id ||
                    instanceEvents[0].ExecutionEpoch !=
                    instance.ExecutionEpoch ||
                    instanceEvents[0].ExecutionEpoch !=
                    branch.Step.ExecutionEpoch ||
                    instanceEvents[0].BranchId != branch.BranchId ||
                    instanceEvents[0].AttemptNo != branch.Step.AttemptNo ||
                    instanceEvents[0].CommandId != sourceEventKey ||
                    instanceEvents[0].SourceEventKey != sourceEventKey ||
                    instanceEvents[0].CorrelationId != sourceCommandId ||
                    instanceEvents[0].FromRevision != fromInstanceRevision ||
                    instanceEvents[0].ToRevision != toInstanceRevision ||
                    string.IsNullOrWhiteSpace(instanceEvents[0].FromState) ||
                    string.IsNullOrWhiteSpace(instanceEvents[0].ToState) ||
                    !DynamicFlowRuntimeStateContract.CanTransitionInstance(
                        instanceEvents[0].FromState!,
                        instanceEvents[0].ToState!) ||
                    instanceEvents[0].ToState != DynamicFlowInstanceStates.Completed ||
                    instanceEvents[0].ReasonCode != "ALL_RUNTIME_STEPS_COMPLETED" ||
                    instanceEvents[0].ActorUserId != receipt.UpdatedByUserId ||
                    !instanceEvents[0].VisibleUnitIds
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(expectedVisibleUnits) ||
                    !instanceEvents[0].AffectedRefs
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(expectedInstanceAffectedRefs) ||
                    !instanceReportPayloadMatches ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "assignmentId",
                        out var instancePayloadAssignmentId) ||
                    instancePayloadAssignmentId != assignmentId ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "terminalStepInstanceId",
                        out var terminalStepInstanceId) ||
                    terminalStepInstanceId != branch.Step.Id ||
                    !TryReadInt32(
                        instanceEvents[0].Payload,
                        "executionEpoch",
                        out var instancePayloadExecutionEpoch) ||
                    instancePayloadExecutionEpoch !=
                    branch.Step.ExecutionEpoch ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "definitionRevision",
                        out var instancePayloadDefinitionRevision) ||
                    instancePayloadDefinitionRevision !=
                    branch.Step.DefinitionRevision ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "flowStepId",
                        out var instancePayloadFlowStepId) ||
                    instancePayloadFlowStepId != branch.Step.FlowStepId ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "branchId",
                        out var instancePayloadBranchId) ||
                    instancePayloadBranchId != branch.BranchId ||
                    !TryReadInt32(
                        instanceEvents[0].Payload,
                        "attemptNo",
                        out var instancePayloadAttemptNo) ||
                    instancePayloadAttemptNo != branch.Step.AttemptNo ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "sourceEventKey",
                        out var instancePayloadSourceEventKey) ||
                    instancePayloadSourceEventKey != sourceEventKey ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "fromState",
                        out var instancePayloadFromState) ||
                    instancePayloadFromState != instanceEvents[0].FromState ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "toState",
                        out var instancePayloadToState) ||
                    instancePayloadToState != instanceEvents[0].ToState ||
                    !TryReadString(
                        instanceEvents[0].Payload,
                        "reasonCode",
                        out var instancePayloadReasonCode) ||
                    instancePayloadReasonCode != instanceEvents[0].ReasonCode ||
                    !FixedEquals(
                        instanceEvents[0].PayloadHash,
                        Hash(instanceEvents[0].Payload.ToJson())))
                {
                    return false;
                }
            }
            else if (instanceEvents.Count != 0)
            {
                return false;
            }
            revisionChains.Add((
                stepInstanceId,
                receiptEvents.Min(item => item.Sequence),
                receiptEvents.Max(item => item.Sequence),
                fromStepRevision,
                toStepRevision,
                fromInstanceRevision,
                toInstanceRevision));
        }

        if (claimedEventIds.Count != events.Count)
            return false;
        long RecoveryRevisionDelta(long afterSequence, long beforeSequence)
            => 4L * baseEvents.Count(item =>
                item.EventType == DynamicFlowRuntimeEventTypes.RecoveryStarted &&
                item.Sequence > afterSequence &&
                item.Sequence < beforeSequence);
        long StepRecoveryRevisionDelta(
            string stepInstanceId,
            long afterSequence,
            long beforeSequence)
            => 4L * baseEvents.Count(recovery =>
                recovery.EventType ==
                DynamicFlowRuntimeEventTypes.RecoveryStarted &&
                recovery.Sequence > afterSequence &&
                recovery.Sequence < beforeSequence &&
                !events.Concat(baseEvents).Any(item =>
                    item.StepInstanceId == stepInstanceId &&
                    item.Sequence < recovery.Sequence &&
                    item.ToState == DynamicFlowStepStates.Completed));
        long InstanceRevisionDelta(long afterSequence, long beforeSequence)
            => RecoveryRevisionDelta(afterSequence, beforeSequence) +
               baseEvents.LongCount(item =>
                   item.EventType is
                       DynamicFlowRuntimeEventTypes.SequentialForwardAccepted or
                       DynamicFlowRuntimeEventTypes.ParallelForkAccepted &&
                   item.Sequence > afterSequence &&
                   item.Sequence < beforeSequence);
        var orderedInstanceChains = revisionChains
            .OrderBy(item => item.FirstSequence)
            .ToArray();
        if (orderedInstanceChains.Zip(
                orderedInstanceChains.Skip(1),
                (left, right) =>
                    left.ToInstanceRevision +
                    InstanceRevisionDelta(left.LastSequence, right.FirstSequence) ==
                    right.FromInstanceRevision)
            .Any(matches => !matches) ||
            orderedInstanceChains[^1].ToInstanceRevision +
            InstanceRevisionDelta(
                orderedInstanceChains[^1].LastSequence,
                long.MaxValue) != instance.Revision)
        {
            return false;
        }
        foreach (var branch in proof)
        {
            var stepEvents = events
                .Concat(baseEvents.Where(item =>
                    item.EventType is
                        DynamicFlowRuntimeEventTypes.SequentialForwardAccepted or
                        DynamicFlowRuntimeEventTypes.ParallelForkAccepted))
                .Where(item => item.StepInstanceId == branch.Step.Id)
                .OrderBy(item => item.Sequence)
                .ToList();
            if (stepEvents.Count == 0)
            {
                if (branch.Step.State != DynamicFlowStepStates.Assigned)
                    return false;
                continue;
            }
            if (stepEvents[0].FromState != DynamicFlowStepStates.Assigned ||
                stepEvents[^1].ToState != branch.Step.State ||
                stepEvents.Any(item =>
                    item.FromRevision is null ||
                    item.ToRevision is null) ||
                stepEvents.Zip(stepEvents.Skip(1), (left, right) =>
                        left.ToState == right.FromState &&
                        left.ToRevision +
                        StepRecoveryRevisionDelta(
                            branch.Step.Id,
                            left.Sequence,
                            right.Sequence) ==
                        right.FromRevision)
                    .Any(matches => !matches))
            {
                return false;
            }
            if (stepEvents[^1].ToRevision +
                StepRecoveryRevisionDelta(
                    branch.Step.Id,
                    stepEvents[^1].Sequence,
                    long.MaxValue) != branch.Step.Revision)
            {
                return false;
            }
        }

        var completionEvents = events.Count(item =>
            item.EventType == DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted);
        return instance.State == DynamicFlowInstanceStates.Completed
            ? completionEvents == 1
            : completionEvents == 0 &&
              instance.State == DynamicFlowInstanceStates.Active;
    }

    private static bool ValidateJoinAllStateProjectionLedger(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> receipts,
        IReadOnlyList<DynamicFlowRuntimeEvent> events)
    {
        var eventById = events
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            var result = receipt.ResultSnapshot;
            if (result is null ||
                receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
                receipt.ScopeId != instance.Id ||
                receipt.FlowInstanceId != instance.Id ||
                receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
                receipt.CommandType is not (
                    DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle or
                    DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete or
                    DynamicFlowRuntimeStateProjectionOperations.StateReconcile) ||
                !FixedEquals(receipt.ResultSnapshotHash, Hash(result.ToJson())) ||
                !TryReadString(result, "stepInstanceId", out var stepId) ||
                !TryReadString(result, "assignmentId", out var assignmentId) ||
                proof.SingleOrDefault(branch =>
                    branch.Step.Id == stepId &&
                    branch.AssignmentId == assignmentId) is not { } branch ||
                !result.TryGetValue("eventIds", out var eventIdsValue) ||
                !eventIdsValue.IsBsonArray)
            {
                return false;
            }

            var eventIds = eventIdsValue.AsBsonArray
                .Select(value => value.AsString)
                .ToArray();
            if (eventIds.Length == 0 ||
                eventIds.Distinct(StringComparer.Ordinal).Count() !=
                eventIds.Length)
            {
                return false;
            }
            foreach (var eventId in eventIds)
            {
                if (!claimed.Add(eventId) ||
                    !eventById.TryGetValue(eventId, out var matches) ||
                    matches.Length != 1 ||
                    matches[0].FlowInstanceId != instance.Id ||
                    matches[0].CommandId != receipt.CommandId ||
                    !FixedEquals(
                        matches[0].PayloadHash,
                        Hash(matches[0].Payload.ToJson())))
                {
                    return false;
                }
            }

            var receiptEvents = eventIds
                .Select(eventId => eventById[eventId][0])
                .ToArray();
            var stepEvents = receiptEvents
                .Where(item =>
                    item.StepInstanceId == branch.Step.Id &&
                    item.EventType is
                        DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged or
                        DynamicFlowRuntimeStateProjectionEventTypes
                            .AssignmentCompletionProjected or
                        DynamicFlowRuntimeStateProjectionEventTypes
                            .ReportLifecycleProjected or
                        DynamicFlowRuntimeStateProjectionEventTypes.StateReconciled)
                .OrderBy(item => item.Sequence)
                .ToArray();
            if (stepEvents.Length == 0 ||
                stepEvents[0].FromRevision !=
                result["fromStepRevision"].ToInt64() ||
                stepEvents[^1].ToRevision !=
                result["toStepRevision"].ToInt64())
            {
                return false;
            }
        }

        if (claimed.Count != events.Count ||
            events.GroupBy(item => item.Sequence).Any(group => group.Count() != 1))
        {
            return false;
        }
        var contributionEvents = events
            .Where(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.JoinContributionAccepted)
            .ToArray();
        var satisfiedEvents = events
            .Where(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.JoinAllSatisfied)
            .ToArray();
        var completedEvents = events
            .Where(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted)
            .ToArray();
        var completedContributionSteps = proof
            .Where(branch =>
                branch.Step.FlowStepId != instance.EntryFlowStepId &&
                branch.Step.State == DynamicFlowStepStates.Completed)
            .ToArray();
        if (contributionEvents.Length != completedContributionSteps.Length ||
            contributionEvents.Select(item => item.ContributionId)
                .Any(string.IsNullOrWhiteSpace) ||
            contributionEvents.Select(item => item.ContributionId!)
                .Distinct(StringComparer.Ordinal)
                .Count() != contributionEvents.Length ||
            contributionEvents.Any(item =>
                completedContributionSteps.All(branch =>
                    branch.Step.Id != item.StepInstanceId ||
                    branch.Step.GatewayInstanceId != item.GatewayInstanceId ||
                    branch.Step.ContributionId != item.ContributionId)))
        {
            return false;
        }

        if (instance.State == DynamicFlowInstanceStates.Completed)
        {
            return completedContributionSteps.Length == 2 &&
                   satisfiedEvents.Length == 1 &&
                   completedEvents.Length == 1 &&
                   satisfiedEvents[0].GatewayInstanceId is not null &&
                   completedEvents[0].ReasonCode == "JOIN_ALL_SATISFIED";
        }
        return instance.State == DynamicFlowInstanceStates.Active &&
               satisfiedEvents.Length == 0 &&
               completedEvents.Length == 0 &&
               completedContributionSteps.Length < 2;
    }

    private static bool ValidateJoinQuorumStateProjectionLedger(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> receipts,
        IReadOnlyList<DynamicFlowRuntimeEvent> events)
    {
        var eventById = events
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            if (receipt.ResultSnapshot is not { } result)
            {
                return false;
            }
            var impossibleReceipt =
                TryReadString(
                    result,
                    "reasonCode",
                    out var receiptReason) &&
                receiptReason == "QUORUM_IMPOSSIBLE";
            if (
                receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
                receipt.ScopeId != instance.Id ||
                receipt.FlowInstanceId != instance.Id ||
                receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
                !FixedEquals(receipt.ResultSnapshotHash, Hash(result.ToJson())) ||
                !impossibleReceipt &&
                (!TryReadString(result, "stepInstanceId", out var stepId) ||
                 proof.All(branch => branch.Step.Id != stepId)) ||
                !result.TryGetValue("eventIds", out var eventIdsValue) ||
                !eventIdsValue.IsBsonArray)
            {
                return false;
            }
            foreach (var eventId in eventIdsValue.AsBsonArray.Select(value => value.AsString))
            {
                if (!claimed.Add(eventId) ||
                    !eventById.TryGetValue(eventId, out var matches) ||
                    matches.Length != 1 ||
                    matches[0].CommandId != receipt.CommandId ||
                    !FixedEquals(matches[0].PayloadHash, Hash(matches[0].Payload.ToJson())))
                {
                    return false;
                }
            }
        }
        if (claimed.Count != events.Count ||
            events.GroupBy(item => item.Id).Any(group => group.Count() != 1) ||
            events.GroupBy(item => item.Sequence).Any(group => group.Count() != 1))
        {
            return false;
        }

        var releases = events.Count(item =>
            item.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.JoinQuorumSatisfied);
        var completions = events.Count(item =>
            item.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted);
        var impossibleEvents = events.Count(item =>
            item.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.JoinQuorumImpossible);
        var failedEvents = events.Count(item =>
            item.EventType ==
            DynamicFlowRuntimeStateProjectionEventTypes.InstanceFailed);
        var lateIds = events
            .Where(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes.JoinLateIgnored)
            .Select(item => item.ContributionId)
            .ToArray();
        if (lateIds.Any(string.IsNullOrWhiteSpace) ||
            lateIds.Distinct(StringComparer.Ordinal).Count() != lateIds.Length ||
            instance.State != DynamicFlowInstanceStates.Failed &&
            proof.Any(branch =>
                branch.Step.FlowStepId != instance.EntryFlowStepId &&
                branch.Step.State is not (
                    DynamicFlowStepStates.Completed or
                    DynamicFlowStepStates.CancelledByGateway)))
        {
            return false;
        }
        return instance.State == DynamicFlowInstanceStates.Completed
            ? releases == 1 &&
              completions == 1 &&
              impossibleEvents == 0 &&
              failedEvents == 0
            : instance.State == DynamicFlowInstanceStates.Failed &&
              releases == 0 &&
              completions == 0 &&
              impossibleEvents == 1 &&
              failedEvents == 1;
    }

    private static bool ValidateSubflowStateProjectionLedger(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> receipts,
        IReadOnlyList<DynamicFlowRuntimeEvent> events,
        IReadOnlyList<DynamicFlowRuntimeEvent> baseEvents)
    {
        if (proof.Count != 1)
            return false;
        var branch = proof[0];
        var step = branch.Step;
        var launchEvents = baseEvents
            .Where(item =>
                item.EventType ==
                DynamicFlowSubflowTopologyContract.ChildLaunchedEvent)
            .ToArray();
        var eventById = events
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var claimedEventIds = new HashSet<string>(StringComparer.Ordinal);
        var allowedCommands = new HashSet<string>(
            new[]
            {
                DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle,
                DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete,
                DynamicFlowRuntimeStateProjectionOperations.StateReconcile,
                DynamicFlowRuntimeStateProjectionOperations.SubflowChildTerminal
            },
            StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            if (receipt.ResultSnapshot is null ||
                receipt.ScopeKind != DynamicFlowCommandScopeKinds.Instance ||
                receipt.ScopeId != instance.Id ||
                receipt.WorkId != instance.WorkId ||
                receipt.FlowTemplateVersionId != instance.FlowTemplateVersionId ||
                receipt.FlowInstanceId != instance.Id ||
                receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
                !allowedCommands.Contains(receipt.CommandType) ||
                !IsSha256(receipt.RequestHash) ||
                !IsSha256(receipt.CommandIdentityHash) ||
                !IsSha256(receipt.SnapshotToken) ||
                !FixedEquals(
                    receipt.ResultSnapshotHash,
                    Hash(receipt.ResultSnapshot.ToJson())) ||
                !TryReadString(
                    receipt.ResultSnapshot,
                    "flowInstanceId",
                    out var receiptInstanceId) ||
                receiptInstanceId != instance.Id ||
                !TryReadString(
                    receipt.ResultSnapshot,
                    "stepInstanceId",
                    out var receiptStepId) ||
                receiptStepId != step.Id ||
                !TryReadString(
                    receipt.ResultSnapshot,
                    "assignmentId",
                    out var receiptAssignmentId) ||
                receiptAssignmentId != branch.AssignmentId ||
                !TryReadString(
                    receipt.ResultSnapshot,
                    "commandType",
                    out var receiptCommandType) ||
                receiptCommandType != receipt.CommandType ||
                !receipt.ResultSnapshot.TryGetValue(
                    "eventIds",
                    out var eventIdsValue) ||
                !eventIdsValue.IsBsonArray)
            {
                return false;
            }
            var eventIds = eventIdsValue.AsBsonArray
                .Where(value => value.IsString)
                .Select(value => value.AsString)
                .ToArray();
            if (eventIds.Length == 0 ||
                eventIds.Length != eventIdsValue.AsBsonArray.Count ||
                eventIds.Distinct(StringComparer.Ordinal).Count() != eventIds.Length)
            {
                return false;
            }
            foreach (var eventId in eventIds)
            {
                if (!claimedEventIds.Add(eventId) ||
                    !eventById.TryGetValue(eventId, out var matches) ||
                    matches.Length != 1)
                {
                    return false;
                }
            }
            if (receipt.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.SubflowChildTerminal)
            {
                if (receipt.Id != StableObjectId(
                        $"{DynamicFlowCommandScopeKinds.Instance}\n{instance.Id}\n{receipt.CommandType}\n{receipt.CommandId}") ||
                    !TryReadString(
                        receipt.ResultSnapshot,
                        "childInstanceId",
                        out var childInstanceId) ||
                    childInstanceId != step.ChildInstanceId ||
                    !TryReadString(
                        receipt.ResultSnapshot,
                        "childState",
                        out var childState) ||
                    childState != step.ChildState ||
                    !TryReadString(
                        receipt.ResultSnapshot,
                        "childOutcomeCode",
                        out var childOutcomeCode) ||
                    childOutcomeCode != step.ChildOutcomeCode ||
                    childState is not (
                        DynamicFlowInstanceStates.Completed or
                        DynamicFlowInstanceStates.Failed or
                        DynamicFlowInstanceStates.Terminated))
                {
                    return false;
                }
            }
        }

        if (claimedEventIds.Count != events.Count ||
            events.Any(item =>
                item.FlowInstanceId != instance.Id ||
                !FixedEquals(item.PayloadHash, Hash(item.Payload.ToJson()))) ||
            events.GroupBy(item => item.Sequence).Any(group => group.Count() != 1))
        {
            return false;
        }

        var stepEvents = events
            .Concat(launchEvents)
            .Where(item => item.StepInstanceId == step.Id)
            .OrderBy(item => item.Sequence)
            .ToArray();
        for (var index = 1; index < stepEvents.Length; index++)
        {
            if (stepEvents[index - 1].ToRevision !=
                    stepEvents[index].FromRevision ||
                stepEvents[index - 1].ToState !=
                    stepEvents[index].FromState)
            {
                return false;
            }
        }
        if (stepEvents.Length == 0)
        {
            if (step.State != DynamicFlowStepStates.Assigned ||
                step.Revision != 1)
            {
                return false;
            }
        }
        else if (stepEvents[^1].ToRevision != step.Revision ||
                 stepEvents[^1].ToState != step.State)
        {
            return false;
        }

        if (step.ChildInstanceId is null)
        {
            if (launchEvents.Length != 0 ||
                step.ChildFlowTemplateId is not null ||
                step.ChildFlowVersionId is not null ||
                step.ChildState is not null ||
                step.ChildOutcomeCode is not null ||
                step.State is DynamicFlowStepStates.WaitingChild or
                    DynamicFlowStepStates.Failed or
                    DynamicFlowStepStates.Terminated)
            {
                return false;
            }
        }
        else
        {
            if (launchEvents.Length != 1 ||
                step.ChildFlowTemplateId is null ||
                step.ChildFlowVersionId is null ||
                step.ChildLinkedAtUtc is null ||
                step.State is not (
                    DynamicFlowStepStates.WaitingChild or
                    DynamicFlowStepStates.Completed or
                    DynamicFlowStepStates.Failed or
                    DynamicFlowStepStates.Terminated))
            {
                return false;
            }
            var launch = launchEvents[0];
            if (launch.Id !=
                    DynamicFlowSubflowTopologyContract.BuildParentEventId(
                        instance.Id,
                        launch.CommandId,
                        DynamicFlowSubflowTopologyContract.ChildLaunchedEvent) ||
                launch.FromState != DynamicFlowStepStates.Approved ||
                launch.ToState != DynamicFlowStepStates.WaitingChild ||
                launch.FromRevision is null ||
                launch.ToRevision != launch.FromRevision + 1 ||
                !TryReadString(
                    launch.Payload,
                    "parentInstanceId",
                    out var parentInstanceId) ||
                parentInstanceId != instance.Id ||
                !TryReadString(
                    launch.Payload,
                    "parentStepInstanceId",
                    out var parentStepInstanceId) ||
                parentStepInstanceId != step.Id ||
                !TryReadString(
                    launch.Payload,
                    "childInstanceId",
                    out var launchedChildInstanceId) ||
                launchedChildInstanceId != step.ChildInstanceId ||
                !TryReadString(
                    launch.Payload,
                    "childFlowTemplateId",
                    out var launchedChildTemplateId) ||
                launchedChildTemplateId != step.ChildFlowTemplateId ||
                !TryReadString(
                    launch.Payload,
                    "childFlowVersionId",
                    out var launchedChildVersionId) ||
                launchedChildVersionId != step.ChildFlowVersionId)
            {
                return false;
            }
        }

        var expectedState = ExpectedRuntimeInstanceState(instance, [step]);
        return instance.State == expectedState &&
               (step.State == DynamicFlowStepStates.WaitingChild
                   ? step.ChildState is
                       DynamicFlowInstanceStates.Materializing or
                       DynamicFlowInstanceStates.Active
                   : step.State == DynamicFlowStepStates.Completed
                       ? step.ChildState ==
                         DynamicFlowInstanceStates.Completed
                       : step.State == DynamicFlowStepStates.Failed
                           ? step.ChildState ==
                             DynamicFlowInstanceStates.Failed
                           : step.State !=
                                 DynamicFlowStepStates.Terminated ||
                             step.ChildState ==
                             DynamicFlowInstanceStates.Terminated);
    }

    private static bool ValidateRuntimeBaseEvents(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        DynamicFlowRuntimeCommandReceipt receipt,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> forwardReceipts,
        IReadOnlyList<DynamicFlowRuntimeOutboxItem> outboxItems,
        IReadOnlyList<DynamicFlowRuntimeEvent> events)
    {
        if (IsParallelFanOutArchetype(instance.ArchetypeId))
        {
            return ValidateParallelForkBaseEvents(
                instance,
                proof,
                receipt,
                forwardReceipts,
                outboxItems,
                events);
        }
        var entryProof = proof
            .Where(branch =>
                branch.Step.FlowStepId == instance.EntryFlowStepId)
            .OrderBy(branch => branch.Ordinal)
            .ToArray();
        if (entryProof.Length == 0 ||
            !TryBuildForwardReceiptIndex(
                instance,
                proof,
                forwardReceipts,
                out var forwardReceiptByStepId))
        {
            return false;
        }

        var allowedTypes = new HashSet<string>(
            new[]
            {
                DynamicFlowRuntimeEventTypes.LaunchIntentCommitted,
                DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized,
                DynamicFlowRuntimeEventTypes.SequentialForwardAccepted,
                DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized,
                DynamicFlowSubflowTopologyContract.ChildLaunchedEvent,
                DynamicFlowRuntimeEventTypes.RecoveryStarted,
                DynamicFlowRuntimeEventTypes.RecoveryReconciled,
                DynamicFlowRuntimeEventTypes.RecoveryCompleted
            },
            StringComparer.Ordinal);
        if (events.Count <
            entryProof.Length +
            1 +
            2 * forwardReceipts.Count ||
            events.Any(item =>
                item.FlowInstanceId != instance.Id ||
                item.Sequence <= 0 ||
                !allowedTypes.Contains(item.EventType) ||
                !FixedEquals(item.PayloadHash, Hash(item.Payload.ToJson()))) ||
            events.GroupBy(item => item.Sequence).Any(group => group.Count() != 1) ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized) !=
            entryProof.Length ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.SequentialForwardAccepted) !=
            forwardReceipts.Count ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized) !=
            forwardReceipts.Count)
        {
            return false;
        }

        var childLaunchEvents = events
            .Where(item =>
                item.EventType ==
                DynamicFlowSubflowTopologyContract.ChildLaunchedEvent)
            .ToArray();
        if (instance.ArchetypeId ==
            DynamicFlowSubflowTopologyContract.ArchetypeId)
        {
            var childStep = proof.Count == 1 ? proof[0] : null;
            var childLaunch = childLaunchEvents.SingleOrDefault();
            if (proof.Count != 1 ||
                childStep is null ||
                (childStep.Step.ChildInstanceId is null
                    ? childLaunchEvents.Length != 0
                    : childLaunchEvents.Length != 1) ||
                childLaunch is not null &&
                (childLaunch.StepInstanceId != childStep.Step.Id ||
                 childLaunch.ExecutionEpoch != instance.ExecutionEpoch ||
                 childLaunch.BranchId != childStep.BranchId ||
                 childLaunch.AttemptNo != childStep.Step.AttemptNo ||
                 childLaunch.FromState != DynamicFlowStepStates.Approved ||
                 childLaunch.ToState !=
                    DynamicFlowStepStates.WaitingChild ||
                 childLaunch.FromRevision is null ||
                 childLaunch.ToRevision !=
                    childLaunch.FromRevision + 1 ||
                 childLaunch.ReasonCode !=
                    "EXACT_PINNED_CHILD_LAUNCHED" ||
                 childLaunch.Id !=
                    DynamicFlowSubflowTopologyContract.BuildParentEventId(
                        instance.Id,
                        childLaunch.CommandId,
                        DynamicFlowSubflowTopologyContract
                            .ChildLaunchedEvent) ||
                 !TryReadString(
                     childLaunch.Payload,
                     "childInstanceId",
                     out var childInstanceId) ||
                 childInstanceId != childStep.Step.ChildInstanceId ||
                 !TryReadString(
                     childLaunch.Payload,
                     "childFlowTemplateId",
                     out var childTemplateId) ||
                 childTemplateId !=
                    childStep.Step.ChildFlowTemplateId ||
                 !TryReadString(
                     childLaunch.Payload,
                     "childFlowVersionId",
                     out var childVersionId) ||
                 childVersionId !=
                    childStep.Step.ChildFlowVersionId))
            {
                return false;
            }
        }
        else if (childLaunchEvents.Length != 0)
        {
            return false;
        }

        var launchEvents = events
            .Where(item =>
                item.EventType == DynamicFlowRuntimeEventTypes.LaunchIntentCommitted)
            .ToList();
        if (launchEvents.Count != 1)
            return false;
        var launch = launchEvents[0];
        var expectedVisibleUnits = entryProof
            .Select(branch => branch.Step.TargetUnitId)
            .Append(instance.IssuerUnitId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (launch.Id != StableObjectId($"{instance.Id}\nevent\nlaunch") ||
            launch.StepInstanceId is not null ||
            launch.Sequence != 1 ||
            launch.CommandId != instance.LaunchCommandId ||
            launch.ActorUserId != instance.IssuerUserId ||
            !launch.VisibleUnitIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedVisibleUnits) ||
            !TryReadString(launch.Payload, "requestHash", out var requestHash) ||
            requestHash != receipt.RequestHash ||
            !TryReadString(launch.Payload, "snapshotToken", out var snapshotToken) ||
            snapshotToken != receipt.SnapshotToken ||
            !TryReadInt32(launch.Payload, "branchCount", out var branchCount) ||
            branchCount != entryProof.Length ||
            !TryReadString(launch.Payload, "periodKey", out var periodKey) ||
            periodKey != instance.PeriodKey)
        {
            return false;
        }

        foreach (var branch in entryProof)
        {
            var branchEvents = events
                .Where(item =>
                    item.EventType ==
                    DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized &&
                    item.StepInstanceId == branch.Step.Id)
                .ToList();
            if (branchEvents.Count != 1)
                return false;
            var item = branchEvents[0];
            var visibleUnits = new[] { branch.Step.TargetUnitId, instance.IssuerUnitId }
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var requiresSequentialIdentity =
                string.Equals(
                    instance.ArchetypeId,
                    DynamicFlowSequentialTopologyContract.ArchetypeId,
                    StringComparison.Ordinal);
            if (item.Id != StableObjectId(
                    $"{instance.Id}\nevent\nbranch\n{branch.Ordinal}") ||
                item.Sequence != branch.Ordinal + 1L ||
                item.CommandId != instance.LaunchCommandId ||
                item.ActorUserId != instance.IssuerUserId ||
                requiresSequentialIdentity &&
                (item.ExecutionEpoch != branch.Step.ExecutionEpoch ||
                 item.BranchId != branch.BranchId ||
                 item.AttemptNo != branch.Step.AttemptNo) ||
                !item.VisibleUnitIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(visibleUnits) ||
                !TryReadString(item.Payload, "assignmentId", out var assignmentId) ||
                assignmentId != branch.AssignmentId ||
                !TryReadString(item.Payload, "targetUnitId", out var targetUnitId) ||
                targetUnitId != branch.Step.TargetUnitId ||
                !TryReadInt32(item.Payload, "branchOrdinal", out var ordinal) ||
                ordinal != branch.Ordinal ||
                requiresSequentialIdentity &&
                (!TryReadInt32(
                     item.Payload,
                     "executionEpoch",
                     out var entryExecutionEpoch) ||
                 entryExecutionEpoch != branch.Step.ExecutionEpoch ||
                 !TryReadString(
                     item.Payload,
                     "branchId",
                     out var entryBranchId) ||
                 entryBranchId != branch.BranchId ||
                 !TryReadInt32(
                     item.Payload,
                     "attemptNo",
                     out var entryAttemptNo) ||
                 entryAttemptNo != branch.Step.AttemptNo))
            {
                return false;
            }
        }

        foreach (var pair in forwardReceiptByStepId)
        {
            var next = proof.Single(branch =>
                branch.Step.Id == pair.Key);
            var forwardReceipt = pair.Value;
            var parent = proof.Single(branch =>
                branch.Step.Id ==
                forwardReceipt.ResultSnapshot!["stepInstanceId"].AsString);
            var acceptedEventId =
                forwardReceipt.ResultSnapshot!["eventId"].AsString;
            var acceptedEvents = events
                .Where(item =>
                    item.Id == acceptedEventId &&
                    item.EventType ==
                    DynamicFlowRuntimeEventTypes.SequentialForwardAccepted)
                .ToArray();
            var materializedEventId = StableObjectId(
                $"{instance.Id}\nevent\nsequential-materialized\n{next.Step.Id}");
            var materializedEvents = events
                .Where(item =>
                    item.Id == materializedEventId &&
                    item.EventType ==
                    DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized)
                .ToArray();
            var outbox = outboxItems.SingleOrDefault(item =>
                item.StepInstanceId == next.Step.Id);
            if (acceptedEvents.Length != 1 ||
                materializedEvents.Length != 1 ||
                outbox is null ||
                !TryReadInt64(
                    outbox.Payload,
                    "materializationEventSequence",
                    out var materializationEventSequence))
            {
                return false;
            }

            var accepted = acceptedEvents[0];
            var materialized = materializedEvents[0];
            var expectedVisible = new[]
                {
                    next.Step.TargetUnitId,
                    instance.IssuerUnitId
                }
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var expectedAcceptedRefs = new[]
                {
                    $"assignment:{parent.AssignmentId}",
                    $"step:{parent.Step.Id}",
                    $"step:{next.Step.Id}",
                    $"assignment:{next.AssignmentId}"
                }
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (accepted.FlowInstanceId != instance.Id ||
                accepted.StepInstanceId != parent.Step.Id ||
                accepted.ExecutionEpoch != instance.ExecutionEpoch ||
                accepted.BranchId != parent.BranchId ||
                accepted.AttemptNo != parent.Step.AttemptNo ||
                accepted.CommandId != forwardReceipt.CommandId ||
                accepted.ActorUserId != forwardReceipt.UpdatedByUserId ||
                accepted.FromState is not (
                    DynamicFlowStepStates.Approved or
                    DynamicFlowStepStates.Completed) ||
                accepted.ToState != DynamicFlowStepStates.Completed ||
                accepted.FromRevision is null ||
                accepted.ToRevision !=
                (accepted.FromState == DynamicFlowStepStates.Approved
                    ? accepted.FromRevision + 1
                    : accepted.FromRevision) ||
                accepted.ReasonCode != "SEQUENTIAL_EDGE_ACTIVATED" ||
                !accepted.VisibleUnitIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(expectedVisible) ||
                !accepted.AffectedRefs
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(expectedAcceptedRefs) ||
                !TryReadString(
                    accepted.Payload,
                    "fromStepInstanceId",
                    out var fromStepInstanceId) ||
                fromStepInstanceId != parent.Step.Id ||
                !TryReadString(
                    accepted.Payload,
                    "fromNodeId",
                    out var fromNodeId) ||
                fromNodeId != parent.Step.FlowStepId ||
                !TryReadString(
                    accepted.Payload,
                    "toStepInstanceId",
                    out var toStepInstanceId) ||
                toStepInstanceId != next.Step.Id ||
                !TryReadString(
                    accepted.Payload,
                    "toNodeId",
                    out var toNodeId) ||
                toNodeId != next.Step.FlowStepId ||
                !TryReadString(
                    accepted.Payload,
                    "transitionId",
                    out var acceptedTransitionId) ||
                acceptedTransitionId != next.Step.ActivatedByTransitionId ||
                !TryReadString(
                    accepted.Payload,
                    "branchId",
                    out var acceptedBranchId) ||
                acceptedBranchId != next.BranchId ||
                !TryReadInt32(
                    accepted.Payload,
                    "executionEpoch",
                    out var acceptedExecutionEpoch) ||
                acceptedExecutionEpoch != instance.ExecutionEpoch ||
                !TryReadString(
                    accepted.Payload,
                    "definitionRevision",
                    out var definitionRevision) ||
                definitionRevision != instance.DefinitionRevision ||
                !TryReadString(
                    accepted.Payload,
                    "topologyHash",
                    out var topologyHash) ||
                !FixedEquals(topologyHash, instance.TopologySnapshotHash) ||
                materialized.FlowInstanceId != instance.Id ||
                materialized.StepInstanceId != next.Step.Id ||
                materialized.ExecutionEpoch != instance.ExecutionEpoch ||
                materialized.BranchId != next.BranchId ||
                materialized.AttemptNo != next.Step.AttemptNo ||
                materialized.Sequence != materializationEventSequence ||
                materialized.Sequence != accepted.Sequence + 1 ||
                materialized.CommandId != forwardReceipt.CommandId ||
                materialized.ActorUserId != forwardReceipt.UpdatedByUserId ||
                materialized.FromState is not null ||
                materialized.ToState is not null ||
                materialized.FromRevision is not null ||
                materialized.ToRevision is not null ||
                !materialized.VisibleUnitIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(expectedVisible) ||
                !TryReadString(
                    materialized.Payload,
                    "assignmentId",
                    out var materializedAssignmentId) ||
                materializedAssignmentId != next.AssignmentId ||
                !TryReadString(
                    materialized.Payload,
                    "targetUnitId",
                    out var materializedTargetUnitId) ||
                materializedTargetUnitId != next.Step.TargetUnitId ||
                !TryReadInt32(
                    materialized.Payload,
                    "branchOrdinal",
                    out var materializedOrdinal) ||
                materializedOrdinal != next.Step.StepOrder ||
                !TryReadInt32(
                    materialized.Payload,
                    "executionEpoch",
                    out var materializedExecutionEpoch) ||
                materializedExecutionEpoch != instance.ExecutionEpoch ||
                !TryReadString(
                    materialized.Payload,
                    "transitionId",
                    out var materializedTransitionId) ||
                materializedTransitionId != next.Step.ActivatedByTransitionId)
            {
                return false;
            }
        }

        var recoveryEvents = events
            .Where(item =>
                item.EventType is not (
                    DynamicFlowRuntimeEventTypes.LaunchIntentCommitted or
                    DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized or
                    DynamicFlowRuntimeEventTypes.SequentialForwardAccepted or
                    DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized or
                    DynamicFlowSubflowTopologyContract.ChildLaunchedEvent))
            .ToArray();
        var recoveryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in recoveryEvents)
        {
            if (item.StepInstanceId is not null ||
                item.CommandId != instance.LaunchCommandId ||
                item.ActorUserId != instance.IssuerUserId ||
                item.VisibleUnitIds.Count != 1 ||
                item.VisibleUnitIds[0] != instance.IssuerUnitId ||
                !TryReadInt64(
                    item.Payload,
                    "recoveryEpoch",
                    out var recoveryEpoch) ||
                recoveryEpoch < 0 ||
                item.Id != RecoveryEventId(
                    instance.Id,
                    item.EventType,
                    recoveryEpoch) ||
                !TryReadString(item.Payload, "state", out var recoveryState))
            {
                return false;
            }
            var expectedState = item.EventType switch
            {
                DynamicFlowRuntimeEventTypes.RecoveryStarted =>
                    recoveryState == DynamicFlowInstanceStates.Retrying,
                DynamicFlowRuntimeEventTypes.RecoveryReconciled =>
                    recoveryState == DynamicFlowInstanceStates.Reconciled,
                DynamicFlowRuntimeEventTypes.RecoveryCompleted =>
                    recoveryState is DynamicFlowInstanceStates.Active
                        or DynamicFlowInstanceStates.Completed,
                _ => false
            };
            if (!expectedState ||
                !recoveryKeys.Add($"{recoveryEpoch}\n{item.EventType}"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateParallelForkBaseEvents(
        DynamicFlowInstance instance,
        IReadOnlyList<RuntimeBranchOwnership> proof,
        DynamicFlowRuntimeCommandReceipt launchReceipt,
        IReadOnlyList<DynamicFlowRuntimeCommandReceipt> forwardReceipts,
        IReadOnlyList<DynamicFlowRuntimeOutboxItem> outboxItems,
        IReadOnlyList<DynamicFlowRuntimeEvent> events)
    {
        var entries = proof
            .Where(branch => branch.Step.FlowStepId == instance.EntryFlowStepId)
            .ToArray();
        var branches = proof
            .Where(branch => branch.Step.FlowStepId != instance.EntryFlowStepId)
            .ToArray();
        var allowed = new HashSet<string>(
            new[]
            {
                DynamicFlowRuntimeEventTypes.LaunchIntentCommitted,
                DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized,
                DynamicFlowRuntimeEventTypes.ParallelForkAccepted,
                DynamicFlowRuntimeEventTypes.ForkBranchAssignmentMaterialized,
                DynamicFlowRuntimeEventTypes.RecoveryStarted,
                DynamicFlowRuntimeEventTypes.RecoveryReconciled,
                DynamicFlowRuntimeEventTypes.RecoveryCompleted
            },
            StringComparer.Ordinal);
        if (events.Any(item =>
                !allowed.Contains(item.EventType) ||
                item.FlowInstanceId != instance.Id ||
                !FixedEquals(item.PayloadHash, Hash(item.Payload.ToJson()))) ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.LaunchIntentCommitted) != 1 ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized) !=
            entries.Length ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.ParallelForkAccepted) !=
            forwardReceipts.Count ||
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.ForkBranchAssignmentMaterialized) !=
            branches.Length ||
            !TryBuildParallelForkReceiptIndex(
                instance,
                proof,
                forwardReceipts,
                out var receiptByStepId))
        {
            return false;
        }

        var launch = events.Single(item =>
            item.EventType == DynamicFlowRuntimeEventTypes.LaunchIntentCommitted);
        if (launch.Id != StableObjectId($"{instance.Id}\nevent\nlaunch") ||
            launch.CommandId != launchReceipt.CommandId ||
            launch.ActorUserId != instance.IssuerUserId)
        {
            return false;
        }
        foreach (var entry in entries)
        {
            var materialized = events.SingleOrDefault(item =>
                item.EventType ==
                    DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized &&
                item.StepInstanceId == entry.Step.Id);
            if (materialized is null ||
                materialized.BranchId != entry.BranchId ||
                materialized.CommandId != instance.LaunchCommandId)
            {
                return false;
            }
        }
        foreach (var branch in branches)
        {
            if (!receiptByStepId.TryGetValue(branch.Step.Id, out var receipt) ||
                branch.Step.GatewayInstanceId is null ||
                branch.Step.ContributionId is null)
            {
                return false;
            }
            var acceptedId = receipt.ResultSnapshot!["eventId"].AsString;
            var accepted = events.SingleOrDefault(item =>
                item.Id == acceptedId &&
                item.EventType == DynamicFlowRuntimeEventTypes.ParallelForkAccepted);
            var materializedId = StableObjectId(
                $"{instance.Id}\nevent\nfork-materialized\n{branch.Step.Id}");
            var materialized = events.SingleOrDefault(item =>
                item.Id == materializedId &&
                item.EventType ==
                DynamicFlowRuntimeEventTypes.ForkBranchAssignmentMaterialized);
            var outbox = outboxItems.SingleOrDefault(item =>
                item.StepInstanceId == branch.Step.Id);
            if (accepted is null ||
                materialized is null ||
                outbox is null ||
                accepted.GatewayInstanceId != branch.Step.GatewayInstanceId ||
                accepted.GatewayVersion != 1 ||
                materialized.GatewayInstanceId != branch.Step.GatewayInstanceId ||
                materialized.GatewayVersion != 1 ||
                materialized.ContributionId != branch.Step.ContributionId ||
                materialized.BranchId != branch.BranchId ||
                materialized.CommandId != receipt.CommandId ||
                !TryReadInt64(
                    outbox.Payload,
                    "materializationEventSequence",
                    out var expectedSequence) ||
                materialized.Sequence != expectedSequence)
            {
                return false;
            }
        }
        return true;
    }

    private static bool FrozenUsersMatch(
        IReadOnlyList<UserRef> users,
        IReadOnlyList<DynamicFlowParticipantUserSnapshot> participants)
    {
        if (users.Count != participants.Count)
            return false;
        var expected = participants
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToArray();
        var actual = users
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToArray();
        return actual.Zip(expected).All(pair =>
            pair.First.UserId == pair.Second.UserId &&
            pair.First.Username == pair.Second.Username &&
            pair.First.FullName == pair.Second.FullName &&
            pair.First.UnitId == pair.Second.UnitId &&
            pair.First.UnitSymbol == pair.Second.UnitSymbol &&
            pair.First.UnitShortName == pair.Second.UnitShortName &&
            pair.First.UnitName == pair.Second.UnitName &&
            pair.First.PositionCode == pair.Second.PositionCode &&
            pair.First.PositionName == pair.Second.PositionName);
    }

    private static bool FrozenUserMatches(
        UserRef? user,
        DynamicFlowParticipantUserSnapshot participant)
        => user is not null &&
           user.UserId == participant.UserId &&
           user.Username == participant.Username &&
           user.FullName == participant.FullName &&
           user.UnitId == participant.UnitId &&
           user.UnitSymbol == participant.UnitSymbol &&
           user.UnitShortName == participant.UnitShortName &&
           user.UnitName == participant.UnitName &&
           user.PositionCode == participant.PositionCode &&
           user.PositionName == participant.PositionName;

    private static bool UserRefsMatch(UserRef? actual, UserRef expected)
        => actual is not null &&
           actual.UserId == expected.UserId &&
           actual.Username == expected.Username &&
           actual.FullName == expected.FullName &&
           actual.UnitId == expected.UnitId &&
           actual.UnitSymbol == expected.UnitSymbol &&
           actual.UnitShortName == expected.UnitShortName &&
           actual.UnitName == expected.UnitName &&
           actual.PositionCode == expected.PositionCode &&
           actual.PositionName == expected.PositionName;

    private static bool MatchesCanonicalPeriodLifecycle(
        WorkReportPeriod period,
        IReadOnlyDictionary<string, WorkAssignmentReport> activeReportByPeriodId,
        IReadOnlyDictionary<string, WorkAssignmentReport> projectionTriggerByPeriodId)
    {
        if (!projectionTriggerByPeriodId.TryGetValue(period.Id, out var triggeringReport))
            return MatchesInitialRuntimePeriod(period);

        activeReportByPeriodId.TryGetValue(period.Id, out var authoritativeReport);
        var sourceReport = authoritativeReport ?? triggeringReport;

        var projection = WorkReportLifecycleProjectionPolicy.ResolvePeriod(
            period,
            authoritativeReport,
            triggeringReport,
            DateTime.UtcNow);
        if (sourceReport.LifecycleRevision <= 0)
        {
            return WorkReportLifecycleProjectionPolicy.MatchesPeriodValues(
                period,
                projection);
        }
        return WorkReportLifecycleProjectionPolicy.MatchesPeriod(
            period,
            projection,
            sourceReport.Id,
            sourceReport.LifecycleRevision);
    }

    private static bool MatchesInitialRuntimePeriod(WorkReportPeriod period)
        => period.PeriodKind == WorkReportPeriodKind.Scheduled &&
           period.ReportDate is null &&
           period.StartedDate is null &&
           period.CompletedDate is null &&
           period.PeriodStart is null &&
           period.PeriodEnd is null &&
           period.DueAtUtc is null &&
           period.Status == WorkReportPeriodStatus.Pending &&
           !period.IsOverdue &&
           !period.IsHistoricalData &&
           !period.HistoricalDataApproved &&
           period.HistoricalDataApprovedAtUtc is null &&
           string.IsNullOrWhiteSpace(period.HistoricalDataApprovedByUserId) &&
           string.IsNullOrWhiteSpace(period.CurrentReportId) &&
           string.IsNullOrWhiteSpace(period.SourceLifecycleReportId) &&
           period.SourceLifecycleRevision == 0 &&
           period.SourceLifecycleAppliedAtUtc is null &&
           period.ReportVersionCount == 0 &&
           period.LastDraftSavedAtUtc is null &&
           period.LastSubmittedAtUtc is null &&
           period.LastReviewedAtUtc is null &&
           !period.RequiresLateReason &&
           string.IsNullOrWhiteSpace(period.AcceptedLateReason) &&
           string.IsNullOrWhiteSpace(period.LateReason) &&
           string.IsNullOrWhiteSpace(period.ReviewerComment) &&
           string.IsNullOrWhiteSpace(period.ReviewerEvaluation) &&
           string.IsNullOrWhiteSpace(period.ReturnReason);

    private static bool MatchesAssignmentCompletionProjection(
        WorkAssignment assignment,
        IReadOnlyCollection<DynamicFlowRuntimeCommandReceipt> receipts,
        bool trustedCompletionReceipt)
    {
        var completionReceipts = receipts
            .Where(receipt =>
                receipt.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete &&
                receipt.CommandId == $"assignment-completed:{assignment.Id}")
            .ToArray();
        if (completionReceipts.Length == 0)
        {
            return !assignment.CompletedAtUtc.HasValue &&
                   !assignment.CompletedDate.HasValue &&
                   string.IsNullOrWhiteSpace(assignment.CompletedByUserId) &&
                   assignment.ProgressStatus !=
                   (int)WorkAssignmentProgressStatus.Completed;
        }
        if (completionReceipts.Length != 1 ||
            !trustedCompletionReceipt ||
            completionReceipts[0].Status !=
            DynamicFlowRuntimeCommandStatuses.Succeeded ||
            !TryReadAssignmentCompletionProjection(
                completionReceipts[0],
                out var completion))
        {
            return false;
        }

        return assignment.CompletedAtUtc == completion.CompletedAtUtc &&
               completion.AssignmentId == assignment.Id &&
               assignment.CompletedDate == completion.CompletedDate &&
               assignment.CompletedByUserId == completion.CompletedByUserId &&
               assignment.ProgressStatus ==
               (int)WorkAssignmentProgressStatus.Completed &&
               assignment.ProgressStatusUpdatedAtUtc ==
               completion.CompletedAtUtc;
    }

    private static bool TryReadAssignmentCompletionProjection(
        DynamicFlowRuntimeCommandReceipt receipt,
        out RuntimeAssignmentCompletionProjection projection)
    {
        projection = default!;
        var snapshot = receipt.ResultSnapshot;
        if (snapshot is null ||
            !snapshot.TryGetValue("assignmentId", out var assignmentIdValue) ||
            !assignmentIdValue.IsString ||
            !snapshot.TryGetValue(
                "assignmentCompletedAtUtc",
                out var completedAtValue) ||
            !completedAtValue.IsString ||
            !DateTime.TryParse(
                completedAtValue.AsString,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var completedAtUtc) ||
            !snapshot.TryGetValue(
                "assignmentCompletedDate",
                out var completedDateValue) ||
            !completedDateValue.IsString ||
            !snapshot.TryGetValue(
                "assignmentCompletedByUserId",
                out var completedByValue) ||
            !completedByValue.IsString ||
            string.IsNullOrWhiteSpace(completedByValue.AsString))
        {
            return false;
        }
        DateTime? completedDate = null;
        if (!string.IsNullOrWhiteSpace(completedDateValue.AsString))
        {
            if (!DateTime.TryParse(
                    completedDateValue.AsString,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedCompletedDate))
            {
                return false;
            }
            completedDate = parsedCompletedDate.ToUniversalTime();
        }

        projection = new RuntimeAssignmentCompletionProjection(
            assignmentIdValue.AsString,
            completedAtUtc.ToUniversalTime(),
            completedDate,
            completedByValue.AsString);
        return true;
    }

    private static bool TryReadString(
        BsonDocument document,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!document.TryGetValue(name, out var item) || !item.IsString)
            return false;
        value = item.AsString;
        return true;
    }

    private static bool TryReadInt32(
        BsonDocument document,
        string name,
        out int value)
    {
        value = default;
        if (!document.TryGetValue(name, out var item) || !item.IsInt32)
            return false;
        value = item.AsInt32;
        return true;
    }

    private static bool TryReadInt64(
        BsonDocument document,
        string name,
        out long value)
    {
        value = default;
        if (!document.TryGetValue(name, out var item))
            return false;
        if (item.IsInt64)
        {
            value = item.AsInt64;
            return true;
        }
        if (item.IsInt32)
        {
            value = item.AsInt32;
            return true;
        }
        return false;
    }

    private static bool BsonStringArrayEquals(
        BsonDocument document,
        string name,
        IReadOnlyCollection<string> expected)
    {
        if (!document.TryGetValue(name, out var item) || !item.IsBsonArray)
            return false;
        var actual = item.AsBsonArray;
        return actual.All(value => value.IsString) &&
               actual.Select(value => value.AsString)
                   .OrderBy(value => value, StringComparer.Ordinal)
                   .SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal));
    }

    private async Task<bool> HasExactRuntimeImmutableAuthorityAsync(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyList<DynamicFlowStepInstance> steps,
        CancellationToken ct)
    {
        try
        {
            var launchReceipts = await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x =>
                    x.FlowInstanceId == instance.Id &&
                    x.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                    x.CommandType == "LAUNCH")
                .ToListAsync(ct);
            if (launchReceipts.Count != 1)
                return false;
            var receipt = launchReceipts[0];
            if (receipt.WorkId != instance.WorkId ||
                receipt.FlowTemplateVersionId !=
                instance.FlowTemplateVersionId ||
                receipt.CommandId != instance.LaunchCommandId ||
                instance.Id != StableObjectId(
                    $"{receipt.WorkId}\n{receipt.FlowTemplateVersionId}\n{receipt.CommandId}") ||
                snapshot.Id != StableObjectId($"{instance.Id}\nparticipants"))
            {
                return false;
            }

            var version = await _ctx.DynamicFlowTemplateVersions
                .Find(x =>
                    x.Id == receipt.FlowTemplateVersionId &&
                    !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (version is null ||
                version.Status is not (
                    DynamicFlowTemplateVersionStatuses.Locked or
                    DynamicFlowTemplateVersionStatuses.Archived) ||
                version.SchemaVersion !=
                DynamicFlowDefinitionSchema.CurrentVersion ||
                version.AdapterVersion !=
                DynamicFlowDefinitionSchema.CurrentAdapterVersion ||
                version.MigrationState !=
                DynamicFlowDefinitionMigrationStates.Canonical ||
                !version.DefinitionLockable)
            {
                return false;
            }
            var family = await _ctx.DynamicFlowTemplates
                .Find(x => x.Id == version.TemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            var work = await _ctx.Works
                .Find(x => x.Id == receipt.WorkId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (family is null || work is null)
                return false;
            if (string.IsNullOrWhiteSpace(instance.ParentInstanceId))
            {
                if (instance.ParentStepInstanceId is not null ||
                    instance.RootInstanceId is not null ||
                    instance.AncestryPath.Count != 0 ||
                    instance.AncestryFlowFamilyIds.Count != 0)
                {
                    return false;
                }
            }
            else
            {
                if (!ObjectId.TryParse(instance.ParentInstanceId, out _) ||
                    !ObjectId.TryParse(instance.ParentStepInstanceId, out _) ||
                    !ObjectId.TryParse(instance.RootInstanceId, out _) ||
                    instance.AncestryPath.Count == 0 ||
                    instance.AncestryPath.Count >
                        DynamicFlowSubflowTopologyContract.MaxDepth ||
                    instance.AncestryFlowFamilyIds.Count !=
                        instance.AncestryPath.Count ||
                    instance.AncestryPath[^1] != instance.ParentInstanceId)
                {
                    return false;
                }
                var parent = await _ctx.DynamicFlowInstances
                    .Find(candidate =>
                        candidate.Id == instance.ParentInstanceId &&
                        candidate.WorkId == instance.WorkId &&
                        !candidate.IsDeleted)
                    .FirstOrDefaultAsync(ct);
                var parentStep = await _ctx.DynamicFlowStepInstances
                    .Find(candidate =>
                        candidate.Id == instance.ParentStepInstanceId &&
                        candidate.FlowInstanceId ==
                            instance.ParentInstanceId &&
                        candidate.ChildInstanceId == instance.Id &&
                        candidate.ChildFlowTemplateId ==
                            instance.FlowTemplateId &&
                        candidate.ChildFlowVersionId ==
                            instance.FlowTemplateVersionId &&
                        !candidate.IsDeleted)
                    .FirstOrDefaultAsync(ct);
                if (parent is null ||
                    parentStep is null ||
                    parent.ArchetypeId !=
                        DynamicFlowSubflowTopologyContract.ArchetypeId ||
                    instance.AncestryFlowFamilyIds[^1] !=
                        parent.FlowTemplateId ||
                    instance.RootInstanceId !=
                        (parent.RootInstanceId ?? parent.Id))
                {
                    return false;
                }
            }

            var canonical =
                DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                    version.PayloadJson,
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true,
                        AllowHistoricalCatalogPins: true));
            DynamicFlowSequentialTopology? sequentialTopology = null;
            DynamicFlowParallelForkTopology? parallelForkTopology = null;
            DynamicFlowSubflowTopology? subflowTopology = null;
            if (string.Equals(
                    instance.ArchetypeId,
                    DynamicFlowSequentialTopologyContract.ArchetypeId,
                    StringComparison.Ordinal))
            {
                sequentialTopology = DynamicFlowSequentialTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash);
            }
            else if (IsParallelFanOutArchetype(instance.ArchetypeId))
            {
                parallelForkTopology = RequireParallelFanOutTopology(instance);
            }
            else if (string.Equals(
                         instance.ArchetypeId,
                         DynamicFlowSubflowTopologyContract.ArchetypeId,
                         StringComparison.Ordinal))
            {
                subflowTopology = DynamicFlowSubflowTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash);
            }
            var expectedDefinitionRevision =
                DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                    version.Id,
                    version.VersionNo,
                    version.PayloadHash);
            if (!FixedEquals(canonical.PayloadHash, version.PayloadHash) ||
                canonical.CanonicalJson != version.PayloadJson ||
                version.TemplateId != family.Id ||
                instance.WorkId != work.Id ||
                instance.WorkType != work.Type.ToString() ||
                instance.FlowTemplateId != family.Id ||
                instance.FlowTemplateVersionId != version.Id ||
                instance.FlowTemplateVersionNo != version.VersionNo ||
                !FixedEquals(instance.FlowPayloadHash, version.PayloadHash) ||
                instance.CatalogVersion != version.CatalogVersion ||
                !FixedEquals(
                    instance.CatalogSemanticHash,
                    version.CatalogSemanticHash) ||
                instance.ArchetypeId != canonical.Payload.ArchetypeId ||
                instance.DefinitionRevision != expectedDefinitionRevision ||
                instance.TopologySnapshotJson != canonical.CanonicalJson ||
                !FixedEquals(
                    instance.TopologySnapshotHash,
                    canonical.PayloadHash) ||
                instance.ExecutionEpoch < 1 ||
                instance.EntryFlowStepId !=
                canonical.Payload.EntryStepId ||
                !FixedEquals(
                    Hash(instance.ScheduleIdentityJson),
                    instance.ScheduleIdentityHash))
            {
                return false;
            }

            var rootFormId = string.IsNullOrWhiteSpace(
                    family.RootDynamicFormTemplateId)
                ? null
                : family.RootDynamicFormTemplateId.Trim();
            if (rootFormId is null ||
                rootFormId != version.RootDynamicFormTemplateId ||
                rootFormId !=
                canonical.Payload.RootDynamicFormTemplateId)
            {
                return false;
            }

            var formIds = canonical.Payload.FormNodes
                .Select(node => node.DynamicFormTemplateId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var forms = await _ctx.DynamicFormTemplates
                .Find(x => formIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct);
            if (forms.Count != formIds.Length)
                return false;
            var formsById = forms.ToDictionary(
                form => form.Id,
                StringComparer.Ordinal);
            foreach (var formNode in canonical.Payload.FormNodes)
            {
                if (!formsById.TryGetValue(
                        formNode.DynamicFormTemplateId,
                        out var form) ||
                    !form.IsPublished)
                {
                    return false;
                }
                var published =
                    DynamicFormPublishedSchemaSnapshotBuilder
                        .ValidateAgainstTemplate(form);
                var familyId = string.IsNullOrWhiteSpace(form.FamilyId)
                    ? form.Id
                    : form.FamilyId;
                if (formNode.DynamicFormFamilyId != familyId ||
                    formNode.DynamicFormVersionNo !=
                    Math.Max(1, form.VersionNo) ||
                    !FixedEquals(
                        formNode.DynamicFormSchemaHash,
                        published.Sha256) ||
                    !FixedEquals(
                        formNode.DynamicFormSnapshotHash,
                        published.Sha256))
                {
                    return false;
                }
            }

            var entry = canonical.Payload.Nodes
                .Select((node, index) => new { Node = node, Order = index + 1 })
                .SingleOrDefault(item =>
                    item.Node.NodeId ==
                    canonical.Payload.EntryStepId);
            if (entry is null ||
                entry.Node.NodeKind != DynamicFlowNodeKinds.FormStep ||
                string.IsNullOrWhiteSpace(entry.Node.FormNodeId))
            {
                return false;
            }
            var entryForm = canonical.Payload.FormNodes.SingleOrDefault(node =>
                node.FormNodeId == entry.Node.FormNodeId);
            if (entryForm is null)
            {
                return false;
            }
            if (sequentialTopology is null &&
                parallelForkTopology is null &&
                subflowTopology is null)
            {
                if (steps.Any(step =>
                        step.FlowStepId != entry.Node.NodeId ||
                        step.FlowStepCode != entry.Node.NodeCode ||
                        step.StepOrder != entry.Order ||
                        step.FormNodeId != entryForm.FormNodeId ||
                        step.FormFamilyId != entryForm.DynamicFormFamilyId ||
                        step.FormVersionId !=
                        entryForm.DynamicFormTemplateId ||
                        step.FormVersionNo !=
                        entryForm.DynamicFormVersionNo ||
                        !FixedEquals(
                            step.FormSchemaHash,
                            entryForm.DynamicFormSchemaHash) ||
                        !FixedEquals(
                            step.FormSnapshotHash,
                            entryForm.DynamicFormSnapshotHash)))
                {
                    return false;
                }
            }
            else if (steps.Any(step =>
                         sequentialTopology is not null
                             ? !SequentialStepPinsMatch(
                                 instance,
                                 sequentialTopology,
                                 step)
                             : parallelForkTopology is not null
                                 ? !ParallelForkRuntimeStepPinsMatch(
                                     instance,
                                     parallelForkTopology,
                                     step)
                                 : step.FlowStepId !=
                                       subflowTopology!.ParentNode.NodeId ||
                                   step.FlowStepCode !=
                                       subflowTopology.ParentNode.NodeCode ||
                                   step.StepOrder != 1 ||
                                   step.FormNodeId !=
                                       subflowTopology.ParentForm.FormNodeId ||
                                   step.FormFamilyId !=
                                       subflowTopology.ParentForm
                                           .DynamicFormFamilyId ||
                                   step.FormVersionId !=
                                       subflowTopology.ParentForm
                                           .DynamicFormTemplateId ||
                                   step.FormVersionNo !=
                                       subflowTopology.ParentForm
                                           .DynamicFormVersionNo ||
                                   !FixedEquals(
                                       step.FormSchemaHash,
                                       subflowTopology.ParentForm
                                           .DynamicFormSchemaHash) ||
                                   !FixedEquals(
                                       step.FormSnapshotHash,
                                       subflowTopology.ParentForm
                                           .DynamicFormSnapshotHash)))
            {
                return false;
            }

            var expectedFormsToken = Hash(string.Join(
                "\n",
                canonical.Payload.FormNodes
                    .OrderBy(node => node.FormNodeId, StringComparer.Ordinal)
                    .Select(node =>
                        $"{node.FormNodeId}:{node.DynamicFormSchemaHash}")));
            return snapshot.SourceRevisionTokens.TryGetValue(
                       "catalog",
                       out var catalogToken) &&
                   FixedEquals(catalogToken, version.CatalogSemanticHash) &&
                   snapshot.SourceRevisionTokens.TryGetValue(
                       "flow",
                       out var flowToken) &&
                   FixedEquals(flowToken, version.PayloadHash) &&
                   snapshot.SourceRevisionTokens.TryGetValue(
                       "forms",
                       out var formsToken) &&
                   FixedEquals(formsToken, expectedFormsToken);
        }
        catch (Exception error) when (
            error is InvalidOperationException or
            ArgumentException or
            System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool SequentialStepPinsMatch(
        DynamicFlowInstance instance,
        DynamicFlowSequentialTopology topology,
        DynamicFlowStepInstance step)
    {
        var indexedNode = topology.OrderedNodes
            .Select((node, index) => new { Node = node, Order = index + 1 })
            .SingleOrDefault(item =>
                string.Equals(
                    item.Node.NodeId,
                    step.FlowStepId,
                    StringComparison.Ordinal));
        if (indexedNode is null ||
            !topology.FormsByNodeId.TryGetValue(
                indexedNode.Node.NodeId,
                out var form))
        {
            return false;
        }

        var expectedNextNodeIds = topology.OutgoingByNodeId.TryGetValue(
            indexedNode.Node.NodeId,
            out var outgoing)
            ? new[] { outgoing.ToNodeId }
            : Array.Empty<string>();
        var expectedIncomingTransition = topology.OutgoingByNodeId.Values
            .SingleOrDefault(edge =>
                string.Equals(
                    edge.ToNodeId,
                    indexedNode.Node.NodeId,
                    StringComparison.Ordinal))
            ?.TransitionId;
        var expectedResultOwner =
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
            instance.Id,
            instance.ExecutionEpoch,
            topology.Payload.ResultOwnerStepId ?? indexedNode.Node.NodeId,
            step.BranchId,
            "result");
        var expectedStatisticOwner =
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
            instance.Id,
            instance.ExecutionEpoch,
            topology.Payload.StatisticsOwnerStepId ?? indexedNode.Node.NodeId,
            step.BranchId,
            "statistics");

        return step.FlowInstanceId == instance.Id &&
               step.ExecutionEpoch == instance.ExecutionEpoch &&
               step.DefinitionRevision == instance.DefinitionRevision &&
               step.FlowStepCode == indexedNode.Node.NodeCode &&
               step.StepOrder == indexedNode.Order &&
               step.FormNodeId == form.FormNodeId &&
               step.FormFamilyId == form.DynamicFormFamilyId &&
               step.FormVersionId == form.DynamicFormTemplateId &&
               form.DynamicFormVersionNo.HasValue &&
               step.FormVersionNo == form.DynamicFormVersionNo.Value &&
               !string.IsNullOrWhiteSpace(form.DynamicFormSchemaHash) &&
               FixedEquals(
                   step.FormSchemaHash,
                   form.DynamicFormSchemaHash) &&
               !string.IsNullOrWhiteSpace(form.DynamicFormSnapshotHash) &&
               FixedEquals(
                   step.FormSnapshotHash,
                   form.DynamicFormSnapshotHash) &&
               step.ParticipantSnapshotId == instance.ParticipantSnapshotId &&
               step.NextNodeIds.SequenceEqual(expectedNextNodeIds) &&
               step.IsTerminalNode == (expectedNextNodeIds.Length == 0) &&
               step.ActivatedByTransitionId == expectedIncomingTransition &&
               step.ResultOwnerIdentity == expectedResultOwner &&
               step.StatisticOwnerIdentity == expectedStatisticOwner;
    }

    private static bool TryBuildOwnershipProof(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyList<DynamicFlowStepInstance> steps,
        out List<RuntimeBranchOwnership> proof)
    {
        proof = new List<RuntimeBranchOwnership>();
        if (steps.Count == 0 ||
            snapshot.Id != instance.ParticipantSnapshotId ||
            snapshot.FlowInstanceId != instance.Id ||
            snapshot.IssuerUserId != instance.IssuerUserId ||
            snapshot.IssuerUnitId != instance.IssuerUnitId ||
            !FixedEquals(
                ParticipantSnapshotHash(snapshot),
                instance.ParticipantSnapshotHash) ||
            !snapshot.SourceRevisionTokens.TryGetValue("catalog", out var catalogToken) ||
            catalogToken != instance.CatalogSemanticHash ||
            !snapshot.SourceRevisionTokens.TryGetValue("flow", out var flowToken) ||
            flowToken != instance.FlowPayloadHash ||
            !snapshot.SourceRevisionTokens.TryGetValue("schedule", out var scheduleToken) ||
            scheduleToken != instance.ScheduleIdentityHash ||
            !snapshot.SourceRevisionTokens.TryGetValue("forms", out var formsToken) ||
            !IsSha256(formsToken))
        {
            return false;
        }

        var bindingGroups = snapshot.Bindings
            .GroupBy(binding => binding.TargetUnitId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        if (bindingGroups.Count != snapshot.Bindings.Count)
        {
            return false;
        }

        var stepsByTarget = steps
            .GroupBy(step => step.TargetUnitId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(step => step.StepOrder)
                    .ThenBy(step => step.Id, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);
        if (stepsByTarget.Count != bindingGroups.Count ||
            stepsByTarget.Keys.Any(targetUnitId =>
                !bindingGroups.ContainsKey(targetUnitId)))
        {
            return false;
        }

        if (IsParallelFanOutArchetype(instance.ArchetypeId))
        {
            return TryBuildParallelForkOwnershipProof(
                instance,
                snapshot,
                stepsByTarget,
                bindingGroups,
                proof);
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowReviewLoopTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            return TryBuildReviewLoopOwnershipProof(
                instance,
                snapshot,
                stepsByTarget,
                bindingGroups,
                proof);
        }

        DynamicFlowSequentialTopology? sequentialTopology = null;
        var isSequential =
            string.Equals(
                instance.ArchetypeId,
                DynamicFlowSequentialTopologyContract.ArchetypeId,
                StringComparison.Ordinal);
        var isFinalize =
            string.Equals(
                instance.ArchetypeId,
                DynamicFlowFinalizeTopologyContract.ArchetypeId,
                StringComparison.Ordinal);
        if (isSequential)
        {
            try
            {
                sequentialTopology = DynamicFlowSequentialTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash);
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                System.Text.Json.JsonException)
            {
                return false;
            }
            if (!snapshot.SourceRevisionTokens.TryGetValue(
                    "topology",
                    out var topologyToken) ||
                !FixedEquals(topologyToken, instance.TopologySnapshotHash))
            {
                return false;
            }
        }
        else if (instance.ArchetypeId is not (
                     "FLOW-T01" or
                     "FLOW-T02" or
                     DynamicFlowSupplementalTopologyContract.ArchetypeId or
                     DynamicFlowFinalizeTopologyContract.ArchetypeId or
                     DynamicFlowSubflowTopologyContract.ArchetypeId))
        {
            return false;
        }

        var orderedTargets = bindingGroups.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        for (var targetIndex = 0; targetIndex < orderedTargets.Length; targetIndex++)
        {
            var targetUnitId = orderedTargets[targetIndex];
            var candidates = bindingGroups[targetUnitId];
            if (candidates.Count != 1 ||
                !stepsByTarget.TryGetValue(targetUnitId, out var targetSteps) ||
                targetSteps.Count == 0 ||
                !isSequential && targetSteps.Count != 1 ||
                isSequential &&
                (sequentialTopology is null ||
                 targetSteps.Count > sequentialTopology.OrderedNodes.Count))
            {
                return false;
            }

            var binding = candidates[0];
            var participantIds = binding.Participants
                .Select(user => user.UserId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var assigneeIds = binding.AssigneeUserIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (participantIds.Length == 0 ||
                participantIds.Distinct(StringComparer.Ordinal).Count() != participantIds.Length ||
                assigneeIds.Distinct(StringComparer.Ordinal).Count() != assigneeIds.Length ||
                !participantIds.SequenceEqual(assigneeIds) ||
                binding.Participants.Any(user =>
                    user.UnitId != targetUnitId ||
                    string.IsNullOrWhiteSpace(user.Username) ||
                    string.IsNullOrWhiteSpace(user.FullName)) ||
                binding.RoleCodes.Count != 1 ||
                binding.RoleCodes[0] != DynamicFlowRuntimePlanner.AssignmentFlowRole)
            {
                return false;
            }

            var branchId = isSequential
                ? DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    targetUnitId)
                : isFinalize
                ? DynamicFlowFinalizeTopologyContract.BuildBranchId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    targetUnitId)
                : StableObjectId(
                    $"{instance.Id}\n{targetSteps[0].FlowStepId}\n{targetUnitId}\n{targetSteps[0].AttemptNo}\nbranch");
            for (var stepIndex = 0; stepIndex < targetSteps.Count; stepIndex++)
            {
                var step = targetSteps[stepIndex];
                var stepParticipantIds = step.ParticipantUserIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var expectedNode = isSequential
                    ? sequentialTopology!.OrderedNodes[stepIndex]
                    : null;
                var stepId = isSequential
                    ? DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        expectedNode!.NodeId,
                        branchId,
                        1)
                    : isFinalize
                    ? DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        step.FlowStepId,
                        branchId)
                    : StableObjectId(
                        $"{instance.Id}\n{step.FlowStepId}\n{targetUnitId}\n{step.AttemptNo}\nstep");
                var assignmentId = isSequential
                    ? DynamicFlowSequentialTopologyContract.BuildAssignmentId(stepId)
                    : isFinalize
                    ? DynamicFlowFinalizeTopologyContract.BuildAssignmentId(stepId)
                    : StableObjectId($"{branchId}\nassignment");
                if (stepParticipantIds
                        .Distinct(StringComparer.Ordinal)
                        .Count() != stepParticipantIds.Length ||
                    !participantIds.SequenceEqual(stepParticipantIds) ||
                    step.FlowInstanceId != instance.Id ||
                    step.TargetUnitId != targetUnitId ||
                    step.AttemptNo != 1 ||
                    step.ParticipantSnapshotId != snapshot.Id ||
                    step.Id != stepId ||
                    step.BranchId != branchId ||
                    step.AssignmentId != assignmentId ||
                    step.FormSnapshotHash != step.FormSchemaHash ||
                    step.IsCanonicalEpoch == false ||
                    isSequential &&
                    (step.StepOrder != stepIndex + 1 ||
                     step.FlowStepId != expectedNode!.NodeId ||
                     !SequentialStepPinsMatch(
                         instance,
                         sequentialTopology!,
                         step)) ||
                    !isSequential &&
                    step.FlowStepId != instance.EntryFlowStepId)
                {
                    return false;
                }

                proof.Add(new RuntimeBranchOwnership(
                    targetIndex + 1,
                    step,
                    binding,
                    branchId,
                    assignmentId,
                    participantIds
                        .Select(userId => StableObjectId(
                            $"{assignmentId}\nbinding\n{userId}"))
                        .ToList()));
            }
        }

        return true;
    }

    private static bool TryBuildReviewLoopOwnershipProof(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyDictionary<string, List<DynamicFlowStepInstance>> stepsByTarget,
        IReadOnlyDictionary<string, List<DynamicFlowParticipantBinding>> bindingGroups,
        List<RuntimeBranchOwnership> proof)
    {
        DynamicFlowReviewLoopTopology topology;
        try
        {
            topology = DynamicFlowReviewLoopTopologyContract.Require(
                instance.TopologySnapshotJson,
                instance.TopologySnapshotHash);
        }
        catch (Exception error) when (
            error is InvalidOperationException or
            ArgumentException or
            System.Text.Json.JsonException)
        {
            return false;
        }
        if (!snapshot.SourceRevisionTokens.TryGetValue(
                "topology",
                out var topologyToken) ||
            !FixedEquals(topologyToken, instance.TopologySnapshotHash))
        {
            return false;
        }

        var orderedTargets = bindingGroups.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        for (var targetIndex = 0;
             targetIndex < orderedTargets.Length;
             targetIndex++)
        {
            var targetUnitId = orderedTargets[targetIndex];
            var candidates = bindingGroups[targetUnitId];
            if (candidates.Count != 1 ||
                !stepsByTarget.TryGetValue(targetUnitId, out var targetSteps) ||
                targetSteps.Count is < 1 ||
                targetSteps.Count > topology.MaxReviewCycles)
            {
                return false;
            }

            var binding = candidates[0];
            var participantIds = binding.Participants
                .Select(user => user.UserId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var assigneeIds = binding.AssigneeUserIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (participantIds.Length == 0 ||
                participantIds.Distinct(StringComparer.Ordinal).Count() !=
                participantIds.Length ||
                assigneeIds.Distinct(StringComparer.Ordinal).Count() !=
                assigneeIds.Length ||
                !participantIds.SequenceEqual(assigneeIds) ||
                binding.Participants.Any(user =>
                    user.UnitId != targetUnitId ||
                    string.IsNullOrWhiteSpace(user.Username) ||
                    string.IsNullOrWhiteSpace(user.FullName)) ||
                binding.RoleCodes.Count != 1 ||
                binding.RoleCodes[0] !=
                DynamicFlowRuntimePlanner.AssignmentFlowRole)
            {
                return false;
            }

            var orderedSteps = targetSteps
                .OrderBy(step => step.AttemptNo)
                .ThenBy(step => step.Id, StringComparer.Ordinal)
                .ToArray();
            var branchId =
                DynamicFlowReviewLoopTopologyContract.BuildBranchId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    targetUnitId);
            for (var stepIndex = 0;
                 stepIndex < orderedSteps.Length;
                 stepIndex++)
            {
                var step = orderedSteps[stepIndex];
                var attemptNo = stepIndex + 1;
                var expectedStepId =
                    DynamicFlowReviewLoopTopologyContract
                        .BuildStepInstanceId(
                            instance.Id,
                            instance.ExecutionEpoch,
                            topology.ReviewNode.NodeId,
                            branchId,
                            attemptNo);
                var expectedAssignmentId =
                    DynamicFlowReviewLoopTopologyContract
                        .BuildAssignmentId(expectedStepId);
                var previous = stepIndex == 0
                    ? null
                    : orderedSteps[stepIndex - 1];
                var next = stepIndex + 1 == orderedSteps.Length
                    ? null
                    : orderedSteps[stepIndex + 1];
                var stepParticipantIds = step.ParticipantUserIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (!participantIds.SequenceEqual(stepParticipantIds) ||
                    step.FlowInstanceId != instance.Id ||
                    step.ExecutionEpoch != instance.ExecutionEpoch ||
                    step.DefinitionRevision != instance.DefinitionRevision ||
                    step.FlowStepId != topology.ReviewNode.NodeId ||
                    step.FlowStepCode != topology.ReviewNode.NodeCode ||
                    step.StepOrder != 1 ||
                    step.FormNodeId != topology.ReviewForm.FormNodeId ||
                    step.FormFamilyId !=
                    topology.ReviewForm.DynamicFormFamilyId ||
                    step.FormVersionId !=
                    topology.ReviewForm.DynamicFormTemplateId ||
                    step.FormVersionNo !=
                    topology.ReviewForm.DynamicFormVersionNo ||
                    !FixedEquals(
                        step.FormSchemaHash,
                        topology.ReviewForm.DynamicFormSchemaHash!) ||
                    !FixedEquals(
                        step.FormSnapshotHash,
                        topology.ReviewForm.DynamicFormSnapshotHash!) ||
                    step.TargetUnitId != targetUnitId ||
                    step.ParticipantSnapshotId != snapshot.Id ||
                    step.AttemptNo != attemptNo ||
                    step.ReviewCycleNo != attemptNo ||
                    step.BranchId != branchId ||
                    step.Id != expectedStepId ||
                    step.AssignmentId != expectedAssignmentId ||
                    step.PreviousAttemptStepInstanceId != previous?.Id ||
                    step.PreviousAttemptAssignmentId !=
                    previous?.AssignmentId ||
                    step.SupersededByStepInstanceId != next?.Id ||
                    step.ActivatedByTransitionId is not null ||
                    step.NextNodeIds.Count != 0 ||
                    !step.IsTerminalNode ||
                    step.ResultOwnerIdentity !=
                    DynamicFlowReviewLoopTopologyContract
                        .BuildOwnerIdentity(
                            instance.Id,
                            instance.ExecutionEpoch,
                            topology.ReviewNode.NodeId,
                            branchId,
                            attemptNo,
                            "result") ||
                    step.StatisticOwnerIdentity !=
                    DynamicFlowReviewLoopTopologyContract
                        .BuildOwnerIdentity(
                            instance.Id,
                            instance.ExecutionEpoch,
                            topology.ReviewNode.NodeId,
                            branchId,
                            attemptNo,
                            "statistics"))
                {
                    return false;
                }

                proof.Add(new RuntimeBranchOwnership(
                    targetIndex + 1,
                    step,
                    binding,
                    branchId,
                    expectedAssignmentId,
                    participantIds
                        .Select(userId => StableObjectId(
                            $"{expectedAssignmentId}\nbinding\n{userId}"))
                        .ToList()));
            }
        }

        return proof.Count > 0;
    }

    private static bool TryBuildParallelForkOwnershipProof(
        DynamicFlowInstance instance,
        DynamicFlowParticipantSnapshot snapshot,
        IReadOnlyDictionary<string, List<DynamicFlowStepInstance>> stepsByTarget,
        IReadOnlyDictionary<string, List<DynamicFlowParticipantBinding>> bindingGroups,
        List<RuntimeBranchOwnership> proof)
    {
        DynamicFlowParallelForkTopology topology;
        try
        {
            topology = RequireParallelFanOutTopology(instance);
        }
        catch (Exception error) when (
            error is InvalidOperationException or
            ArgumentException or
            System.Text.Json.JsonException)
        {
            return false;
        }
        if (!snapshot.SourceRevisionTokens.TryGetValue(
                "topology",
                out var topologyToken) ||
            !FixedEquals(topologyToken, instance.TopologySnapshotHash))
        {
            return false;
        }

        var orderedTargets = bindingGroups.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        for (var targetIndex = 0; targetIndex < orderedTargets.Length; targetIndex++)
        {
            var targetUnitId = orderedTargets[targetIndex];
            var bindings = bindingGroups[targetUnitId];
            var targetSteps = stepsByTarget[targetUnitId];
            var isConditional =
                instance.ArchetypeId ==
                DynamicFlowTypedConditionalTopologyContract.ArchetypeId;
            if (bindings.Count != 1 ||
                isConditional && targetSteps.Count is not (1 or 2) ||
                !isConditional && targetSteps.Count is not (1 or 3))
            {
                return false;
            }
            var binding = bindings[0];
            var participantIds = binding.Participants
                .Select(user => user.UserId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (participantIds.Length == 0 ||
                !participantIds.SequenceEqual(
                    binding.AssigneeUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal)))
            {
                return false;
            }

            var rootBranchId =
                DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    targetUnitId);
            var rootStepId =
                DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    topology.EntryNode.NodeId,
                    rootBranchId,
                    1);
            var root = targetSteps.SingleOrDefault(step => step.Id == rootStepId);
            if (root is null ||
                !ParallelForkStepPinsMatch(
                    instance,
                    topology,
                    root,
                    topology.EntryNode,
                    topology.EntryForm,
                    rootBranchId,
                    gatewayInstanceId: null,
                    contributionId: null,
                    transitionId: null,
                    stepOrder: 1,
                    nextNodeIds: new[] { topology.ForkNode.NodeId },
                    terminal: false))
            {
                return false;
            }
            if (isConditional && targetSteps.Count == 2)
            {
                var gatewayId =
                    DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        topology.ForkNode.NodeId,
                        rootBranchId);
                var selected = targetSteps.Single(step => step.Id != rootStepId);
                var selectedBranch = topology.Branches.SingleOrDefault(branch =>
                    branch.Node.NodeId == selected.FlowStepId &&
                    branch.Edge.TransitionId ==
                    selected.ActivatedByTransitionId);
                if (selectedBranch is null)
                    return false;
                var branchId =
                    DynamicFlowParallelForkTopologyContract.BuildBranchId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        gatewayId,
                        selectedBranch.Edge.TransitionId,
                        targetUnitId);
                var contributionId =
                    DynamicFlowParallelForkTopologyContract.BuildContributionId(
                        gatewayId,
                        branchId,
                        selectedBranch.Edge.TransitionId);
                if (!ParallelForkStepPinsMatch(
                        instance,
                        topology,
                        selected,
                        selectedBranch.Node,
                        selectedBranch.Form,
                        branchId,
                        gatewayId,
                        contributionId,
                        selectedBranch.Edge.TransitionId,
                        selectedBranch.StepOrder,
                        Array.Empty<string>(),
                        terminal: true))
                {
                    return false;
                }
            }
            else if (!isConditional && targetSteps.Count == 3)
            {
                var gatewayId =
                    DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        topology.ForkNode.NodeId,
                        rootBranchId);
                foreach (var forkBranch in topology.Branches)
                {
                    var branchId =
                        DynamicFlowParallelForkTopologyContract.BuildBranchId(
                            instance.Id,
                            instance.ExecutionEpoch,
                            gatewayId,
                            forkBranch.Edge.TransitionId,
                            targetUnitId);
                    var contributionId =
                        DynamicFlowParallelForkTopologyContract.BuildContributionId(
                            gatewayId,
                            branchId,
                            forkBranch.Edge.TransitionId);
                    var stepId =
                        DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                            instance.Id,
                            instance.ExecutionEpoch,
                            forkBranch.Node.NodeId,
                            branchId,
                            1);
                    var step = targetSteps.SingleOrDefault(candidate =>
                        candidate.Id == stepId);
                    if (step is null ||
                        !ParallelForkStepPinsMatch(
                            instance,
                            topology,
                            step,
                            forkBranch.Node,
                            forkBranch.Form,
                            branchId,
                            gatewayId,
                            contributionId,
                            forkBranch.Edge.TransitionId,
                            forkBranch.StepOrder,
                            Array.Empty<string>(),
                            terminal: true))
                    {
                        return false;
                    }
                }
            }

            foreach (var step in targetSteps)
            {
                var assignmentId =
                    DynamicFlowParallelForkTopologyContract.BuildAssignmentId(
                        step.Id);
                if (step.AssignmentId != assignmentId ||
                    step.ParticipantSnapshotId != snapshot.Id ||
                    !participantIds.SequenceEqual(
                        step.ParticipantUserIds.OrderBy(
                            value => value,
                            StringComparer.Ordinal)))
                {
                    return false;
                }
                proof.Add(new RuntimeBranchOwnership(
                    targetIndex + 1,
                    step,
                    binding,
                    step.BranchId,
                    assignmentId,
                    participantIds.Select(userId =>
                        StableObjectId($"{assignmentId}\nbinding\n{userId}"))
                        .ToList()));
            }
        }
        return true;
    }

    private static bool ParallelForkStepPinsMatch(
        DynamicFlowInstance instance,
        DynamicFlowParallelForkTopology topology,
        DynamicFlowStepInstance step,
        DynamicFlowTopologyNodeDto node,
        DynamicFlowFormNodeDto form,
        string branchId,
        string? gatewayInstanceId,
        string? contributionId,
        string? transitionId,
        int stepOrder,
        IReadOnlyList<string> nextNodeIds,
        bool terminal)
    {
        var expectedResultOwner =
            DynamicFlowParallelForkTopologyContract.BuildStepOwnerIdentity(
                instance.Id,
                instance.ExecutionEpoch,
                topology.Payload.ResultOwnerStepId ?? node.NodeId,
                branchId,
                "result");
        var expectedStatisticOwner =
            DynamicFlowParallelForkTopologyContract.BuildStepOwnerIdentity(
                instance.Id,
                instance.ExecutionEpoch,
                topology.Payload.StatisticsOwnerStepId ?? node.NodeId,
                branchId,
                "statistics");
        return step.FlowInstanceId == instance.Id &&
               step.FlowStepId == node.NodeId &&
               step.FlowStepCode == node.NodeCode &&
               step.ExecutionEpoch == instance.ExecutionEpoch &&
               step.DefinitionRevision == instance.DefinitionRevision &&
               step.StepOrder == stepOrder &&
               step.FormNodeId == form.FormNodeId &&
               step.FormFamilyId == form.DynamicFormFamilyId &&
               step.FormVersionId == form.DynamicFormTemplateId &&
               step.FormVersionNo == form.DynamicFormVersionNo &&
               step.FormSchemaHash == form.DynamicFormSchemaHash &&
               step.FormSnapshotHash == form.DynamicFormSnapshotHash &&
               step.TargetUnitId.Length > 0 &&
               step.AttemptNo == 1 &&
               step.BranchId == branchId &&
               step.GatewayInstanceId == gatewayInstanceId &&
               step.GatewayVersion == (gatewayInstanceId is null ? null : 1) &&
               step.ContributionId == contributionId &&
               step.ActivatedByTransitionId == transitionId &&
               step.NextNodeIds.SequenceEqual(nextNodeIds, StringComparer.Ordinal) &&
               step.IsTerminalNode == terminal &&
               step.ResultOwnerIdentity == expectedResultOwner &&
               step.StatisticOwnerIdentity == expectedStatisticOwner;
    }

    private static bool ParallelForkRuntimeStepPinsMatch(
        DynamicFlowInstance instance,
        DynamicFlowParallelForkTopology topology,
        DynamicFlowStepInstance step)
    {
        var rootBranchId =
            DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        if (string.Equals(
                step.FlowStepId,
                topology.EntryNode.NodeId,
                StringComparison.Ordinal))
        {
            return step.Id ==
                   DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                       instance.Id,
                       instance.ExecutionEpoch,
                       topology.EntryNode.NodeId,
                       rootBranchId,
                       1) &&
                   ParallelForkStepPinsMatch(
                       instance,
                       topology,
                       step,
                       topology.EntryNode,
                       topology.EntryForm,
                       rootBranchId,
                       gatewayInstanceId: null,
                       contributionId: null,
                       transitionId: null,
                       stepOrder: 1,
                       nextNodeIds: new[] { topology.ForkNode.NodeId },
                       terminal: false);
        }

        var branch = topology.Branches.SingleOrDefault(item =>
            string.Equals(
                item.Node.NodeId,
                step.FlowStepId,
                StringComparison.Ordinal));
        if (branch is null)
            return false;
        var gatewayInstanceId =
            DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.ForkNode.NodeId,
                rootBranchId);
        var branchId = DynamicFlowParallelForkTopologyContract.BuildBranchId(
            instance.Id,
            instance.ExecutionEpoch,
            gatewayInstanceId,
            branch.Edge.TransitionId,
            step.TargetUnitId);
        var contributionId =
            DynamicFlowParallelForkTopologyContract.BuildContributionId(
                gatewayInstanceId,
                branchId,
                branch.Edge.TransitionId);
        return step.Id ==
               DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                   instance.Id,
                   instance.ExecutionEpoch,
                   branch.Node.NodeId,
                   branchId,
                   1) &&
               ParallelForkStepPinsMatch(
                   instance,
                   topology,
                   step,
                   branch.Node,
                   branch.Form,
                   branchId,
                   gatewayInstanceId,
                   contributionId,
                   branch.Edge.TransitionId,
                   branch.StepOrder,
                    Array.Empty<string>(),
                    terminal: true);
    }

    private static bool IsParallelFanOutArchetype(string? archetypeId)
        => archetypeId is
            DynamicFlowParallelForkTopologyContract.ArchetypeId or
            DynamicFlowJoinAllTopologyContract.ArchetypeId or
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId or
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId;

    private static DynamicFlowParallelForkTopology RequireParallelFanOutTopology(
        DynamicFlowInstance instance)
        => RequireParallelFanOutTopology(
            instance.ArchetypeId,
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);

    private static DynamicFlowParallelForkTopology RequireParallelFanOutTopology(
        string archetypeId,
        string topologySnapshotJson,
        string topologySnapshotHash)
        => archetypeId switch
        {
            DynamicFlowParallelForkTopologyContract.ArchetypeId =>
                DynamicFlowParallelForkTopologyContract.Require(
                    topologySnapshotJson,
                    topologySnapshotHash),
            DynamicFlowJoinAllTopologyContract.ArchetypeId =>
                DynamicFlowJoinAllTopologyContract.Require(
                    topologySnapshotJson,
                    topologySnapshotHash),
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId =>
                DynamicFlowJoinQuorumTopologyContract.Require(
                    topologySnapshotJson,
                    topologySnapshotHash),
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId =>
                DynamicFlowTypedConditionalTopologyContract.Require(
                    topologySnapshotJson,
                    topologySnapshotHash),
            _ => throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_ARCHETYPE_BLOCKED_UNTIL_TARGET_PROMPT")
        };

    private static string ParticipantSnapshotHash(DynamicFlowParticipantSnapshot snapshot)
    {
        var sourceTokens = new BsonDocument(snapshot.SourceRevisionTokens
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BsonElement(pair.Key, pair.Value)));
        var bindings = new BsonArray(snapshot.Bindings
            .OrderBy(binding => binding.TargetUnitId, StringComparer.Ordinal)
            .Select(binding => new BsonDocument
            {
                { "targetUnitId", binding.TargetUnitId },
                {
                    "assigneeUserIds",
                    new BsonArray(binding.AssigneeUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal))
                },
                {
                    "participants",
                    new BsonArray(binding.Participants
                        .OrderBy(user => user.UserId, StringComparer.Ordinal)
                        .Select(ParticipantDocument))
                },
                {
                    "roleCodes",
                    new BsonArray(binding.RoleCodes.OrderBy(
                        value => value,
                        StringComparer.Ordinal))
                }
            }));
        return Hash(new BsonDocument
        {
            { "flowInstanceId", snapshot.FlowInstanceId },
            { "issuerUserId", snapshot.IssuerUserId },
            { "issuerUnitId", snapshot.IssuerUnitId },
            { "bindings", bindings },
            { "sourceRevisionTokens", sourceTokens }
        }.ToJson());
    }

    private static string? OptionalString(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && !value.IsBsonNull
            ? value.AsString
            : null;

    private static string StableObjectId(string seed)
        => Hash(seed)[..24];

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static FilterDefinition<DynamicFlowRuntimeOutboxItem> HeldClaim(
        DynamicFlowRuntimeOutboxItem item,
        bool requireUnexpiredLease = true)
    {
        var filter = Builders<DynamicFlowRuntimeOutboxItem>.Filter;
        var epoch =
            filter.Eq(x => x.RepairEpoch, item.RepairEpoch);
        if (item.RepairEpoch == 0)
            epoch |= filter.Exists(x => x.RepairEpoch, false);
        var held =
            filter.Eq(x => x.Id, item.Id) &
            filter.Eq(
                x => x.Status,
                DynamicFlowRuntimeOutboxStatuses.Processing) &
            filter.Eq(x => x.LeaseId, item.LeaseId) &
            epoch;
        if (requireUnexpiredLease)
            held &= filter.Gt(x => x.LeaseUntilUtc, DateTime.UtcNow);
        return held;
    }

    private static FilterDefinition<DynamicFlowInstance>
        RuntimeMaterializationFenceFilter(
            string flowInstanceId,
            string? runtimeReconcileLeaseId,
            DateTime nowUtc)
    {
        var filter = Builders<DynamicFlowInstance>.Filter;
        var nonTerminal =
            filter.Ne(x => x.State, DynamicFlowInstanceStates.Failed) &
            filter.Ne(x => x.State, DynamicFlowInstanceStates.Terminated);
        FilterDefinition<DynamicFlowInstance> leaseAllowed;
        if (string.IsNullOrWhiteSpace(runtimeReconcileLeaseId))
        {
            leaseAllowed =
                filter.Eq(x => x.RuntimeReconcileLeaseId, null) |
                filter.Exists(x => x.RuntimeReconcileLeaseId, false) |
                filter.Lte(x => x.RuntimeReconcileLeaseExpiresAtUtc, nowUtc);
        }
        else
        {
            leaseAllowed =
                filter.Eq(x => x.RuntimeReconcileLeaseId, runtimeReconcileLeaseId) &
                filter.Gt(x => x.RuntimeReconcileLeaseExpiresAtUtc, nowUtc);
        }

        return filter.Eq(x => x.Id, flowInstanceId) &
               filter.Eq(x => x.IsDeleted, false) &
               nonTerminal &
               leaseAllowed;
    }

    private static FilterDefinition<DynamicFlowInstance>
        RuntimeCompensationFenceFilter(
            string flowInstanceId,
            string runtimeReconcileLeaseId,
            DateTime nowUtc)
    {
        var filter = Builders<DynamicFlowInstance>.Filter;
        return filter.Eq(x => x.Id, flowInstanceId) &
               filter.Eq(
                   x => x.State,
                   DynamicFlowInstanceStates.Failed) &
               filter.Eq(
                   x => x.RuntimeReconcileLeaseId,
                   runtimeReconcileLeaseId) &
               filter.Gt(
                   x => x.RuntimeReconcileLeaseExpiresAtUtc,
                   nowUtc) &
               filter.Eq(x => x.IsDeleted, false);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsDuplicateKey(Exception error)
    {
        if (error is MongoWriteException write &&
            write.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            return true;
        if (error is MongoCommandException command && command.Code == 11000)
            return true;
        return error.InnerException is not null && IsDuplicateKey(error.InnerException);
    }

    private static string StableErrorCode(Exception error)
        => error switch
        {
            DynamicFlowRuntimeInjectedFaultException => "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE",
            MongoException => "DYNAMIC_FLOW_RUNTIME_MONGO_FAILURE",
            _ => "DYNAMIC_FLOW_RUNTIME_MATERIALIZATION_FAILURE"
        };

    private sealed record RuntimeIntent(
        DynamicFlowInstance Instance,
        DynamicFlowParticipantSnapshot Snapshot,
        DynamicFlowRuntimeCommandReceipt Receipt,
        DynamicFlowRuntimeEvent InitialEvent,
        List<RuntimeIntentBranch> Branches);

    private sealed record RuntimeAssignmentCompletionProjection(
        string AssignmentId,
        DateTime CompletedAtUtc,
        DateTime? CompletedDate,
        string CompletedByUserId);

    private sealed class DynamicFlowRuntimeAssignmentTerminalException
        : Exception
    {
        public DynamicFlowRuntimeAssignmentTerminalException(string assignmentId)
            : base("DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_TERMINAL")
        {
            AssignmentId = assignmentId;
        }

        public string AssignmentId { get; }
    }

    private sealed record RuntimeIntentBranch(
        int Ordinal,
        DynamicFlowStepInstance Step,
        List<WorkTemplateAssignee> BindingClaims,
        DynamicFlowRuntimeOutboxItem Outbox);

    private sealed record OwnedArtifactRepairResult(
        List<string> RepairedPeriodIds,
        List<string> StalePeriodIds,
        List<string> RogueAssignmentIds);

    private sealed record RuntimeBranchOwnership(
        int Ordinal,
        DynamicFlowStepInstance Step,
        DynamicFlowParticipantBinding Binding,
        string BranchId,
        string AssignmentId,
        List<string> BindingIds);
}
