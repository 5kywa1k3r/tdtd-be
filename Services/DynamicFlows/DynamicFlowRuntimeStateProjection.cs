using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.DynamicFlows;

public static class DynamicFlowRuntimeStateProjectionOperations
{
    public const string ReportLifecycle = "REPORT_LIFECYCLE_PROJECT";
    public const string AssignmentComplete = "ASSIGNMENT_COMPLETE_PROJECT";
    public const string StateReconcile = "STATE_RECONCILE";
    public const string SubflowChildTerminal =
        "SUBFLOW_CHILD_TERMINAL_PROJECT";
}

public static class DynamicFlowRuntimeStateProjectionEventTypes
{
    public const string StepStateChanged = "STEP_STATE_CHANGED";
    public const string ReportLifecycleProjected = "REPORT_LIFECYCLE_PROJECTED";
    public const string InstanceCompleted = "INSTANCE_COMPLETED";
    public const string AssignmentCompletionProjected = "ASSIGNMENT_COMPLETION_PROJECTED";
    public const string JoinContributionAccepted = "JOIN_CONTRIBUTION_ACCEPTED";
    public const string JoinAllSatisfied = "JOIN_ALL_SATISFIED";
    public const string JoinQuorumSatisfied = "JOIN_QUORUM_SATISFIED";
    public const string JoinBranchCancelled = "JOIN_BRANCH_CANCELLED";
    public const string JoinLateIgnored = "JOIN_LATE_IGNORED";
    public const string JoinQuorumImpossible = "JOIN_QUORUM_IMPOSSIBLE";
    public const string InstanceFailed = "INSTANCE_FAILED";
    public const string InstanceTerminated = "INSTANCE_TERMINATED";
    public const string StateReconciled = "STATE_RECONCILED";
    public const string SubflowChildCompleted =
        DynamicFlowSubflowTopologyContract.ChildCompletedEvent;
    public const string SubflowChildBlocked =
        DynamicFlowSubflowTopologyContract.ChildBlockedEvent;

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[]
        {
            StepStateChanged,
            ReportLifecycleProjected,
            InstanceCompleted,
            AssignmentCompletionProjected,
            JoinContributionAccepted,
            JoinAllSatisfied,
            JoinQuorumSatisfied,
            JoinBranchCancelled,
            JoinLateIgnored,
            JoinQuorumImpossible,
            InstanceFailed,
            InstanceTerminated,
            StateReconciled,
            SubflowChildCompleted,
            SubflowChildBlocked
        },
        StringComparer.Ordinal);
}

public static class DynamicFlowRuntimeStateProjectionFaultPoints
{
    public const string BeforeTransaction = "BEFORE_STATE_PROJECTION_TRANSACTION";
    public const string BeforeReceiptWrite = "BEFORE_STATE_PROJECTION_RECEIPT_WRITE";
    public const string AfterReceiptWrite = "AFTER_STATE_PROJECTION_RECEIPT_WRITE";
    public const string BeforeStepWrite = "BEFORE_STATE_PROJECTION_STEP_WRITE";
    public const string AfterStepWrite = "AFTER_STATE_PROJECTION_STEP_WRITE";
    public const string AfterJoinContributionWrite =
        "AFTER_JOIN_CONTRIBUTION_WRITE";
    public const string AfterJoinGatewayWrite = "AFTER_JOIN_GATEWAY_WRITE";
    public const string BeforeInstanceWrite = "BEFORE_STATE_PROJECTION_INSTANCE_WRITE";
    public const string AfterInstanceWrite = "AFTER_STATE_PROJECTION_INSTANCE_WRITE";
    public const string BeforeEventWrite = "BEFORE_STATE_PROJECTION_EVENT_WRITE";
    public const string AfterEventWrite = "AFTER_STATE_PROJECTION_EVENT_WRITE";
    public const string AfterTransactionCommit = "AFTER_STATE_PROJECTION_TRANSACTION_COMMIT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[]
        {
            BeforeTransaction,
            BeforeReceiptWrite,
            AfterReceiptWrite,
            BeforeStepWrite,
            AfterStepWrite,
            AfterJoinContributionWrite,
            AfterJoinGatewayWrite,
            BeforeInstanceWrite,
            AfterInstanceWrite,
            BeforeEventWrite,
            AfterEventWrite,
            AfterTransactionCommit
        },
        StringComparer.Ordinal);
}

public interface IDynamicFlowRuntimeStateProjectionFaultInjector
{
    void ThrowIfConfigured(string sourceCommandId, string faultPoint);
}

public sealed class DynamicFlowRuntimeStateProjectionInjectedFaultException(string message)
    : Exception(message);

public sealed class DynamicFlowRuntimeStateProjectionFaultInjector
    : IDynamicFlowRuntimeStateProjectionFaultInjector
{
    public const string CommandIdConfigurationKey =
        "DynamicFlowRuntime:TestingStateProjectionFault:CommandId";
    public const string PointsConfigurationKey =
        "DynamicFlowRuntime:TestingStateProjectionFault:Points";
    public const string FailureMessage =
        "TEST_ONLY_DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_FAILURE";

    private readonly string? _commandId;
    private readonly IReadOnlySet<string> _points;
    private readonly ConcurrentDictionary<string, byte> _consumed = new(StringComparer.Ordinal);

    public DynamicFlowRuntimeStateProjectionFaultInjector(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            _points = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        _commandId = Normalize(configuration[CommandIdConfigurationKey]);
        var section = configuration.GetSection(PointsConfigurationKey);
        var configured = section.GetChildren()
            .Select(child => Normalize(child.Value))
            .Where(value => value is not null)
            .Cast<string>()
            .Concat((section.Value ?? string.Empty)
                .Split(
                    new[] { ',', ';' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(value => value is not null)
                .Cast<string>())
            .Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unsupported = configured
            .Where(value => !DynamicFlowRuntimeStateProjectionFaultPoints.All.Contains(value))
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unsupported Dynamic Flow state projection fault point(s): {string.Join(", ", unsupported)}.");
        }

        _points = configured.ToHashSet(StringComparer.Ordinal);
    }

    public void ThrowIfConfigured(string sourceCommandId, string faultPoint)
    {
        if (!DynamicFlowRuntimeStateProjectionFaultPoints.All.Contains(faultPoint))
            throw new ArgumentOutOfRangeException(nameof(faultPoint), faultPoint, "Unknown state projection fault point.");
        if (!string.Equals(_commandId, Normalize(sourceCommandId), StringComparison.Ordinal) ||
            !_points.Contains(faultPoint) ||
            !_consumed.TryAdd(faultPoint, 0))
        {
            return;
        }

        throw new DynamicFlowRuntimeStateProjectionInjectedFaultException(
            $"{FailureMessage}:{faultPoint}");
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed record DynamicFlowRuntimeStateProjectionResult(
    string FlowInstanceId,
    string StepInstanceId,
    string AssignmentId,
    string CommandType,
    string SourceCommandId,
    string SourceEventKey,
    string FromState,
    string ToState,
    long FromStepRevision,
    long ToStepRevision,
    long FromInstanceRevision,
    long ToInstanceRevision,
    IReadOnlyList<string> EventIds,
    bool Replayed,
    bool InstanceCompleted,
    string ProjectionFingerprint);

public sealed record DynamicFlowRuntimeStateReconcileResult(
    string FlowInstanceId,
    bool Apply,
    int AssignmentCount,
    int DriftCount,
    int ProjectedCount,
    IReadOnlyList<string> DriftAssignmentIds,
    IReadOnlyList<string> PendingReportIds,
    IReadOnlyList<string> UnprojectedReportIds);

public interface IDynamicFlowRuntimeStateProjector
{
    Task ProjectReportLifecycleAsync(
        WorkAssignmentReport sourceReport,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pendingEntries,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimeStateProjectionResult?> ProjectAssignmentCompletionAsync(
        string assignmentId,
        string actorUserId,
        CancellationToken ct = default);

    Task<bool> IsAssignmentCompletionProjectionReadyAsync(
        string assignmentId,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimeStateReconcileResult> ReconcileInstanceAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        CancellationToken ct = default);

    Task ProjectSubflowParentTerminalAsync(
        string childInstanceId,
        string actorUserId,
        CancellationToken ct = default);
}

public sealed class DynamicFlowRuntimeStateProjector : IDynamicFlowRuntimeStateProjector
{
    private const int MaxRaceAttempts = 5;

    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly IDynamicFlowRuntimeStateProjectionFaultInjector _faults;

    public DynamicFlowRuntimeStateProjector(
        MongoDbContext ctx,
        IDynamicFlowDefinitionTransactionRunner transactions,
        IDynamicFlowRuntimeStateProjectionFaultInjector faults)
    {
        _ctx = ctx;
        _transactions = transactions;
        _faults = faults;
    }

    public async Task ProjectReportLifecycleAsync(
        WorkAssignmentReport sourceReport,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pendingEntries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReport);
        ArgumentNullException.ThrowIfNull(pendingEntries);
        if (pendingEntries.Count == 0)
            return;

        var assignment = await _ctx.WorkAssignments
            .Find(x =>
                x.Id == sourceReport.WorkAssignmentId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return;

        var canonicalStepCount = await _ctx.DynamicFlowStepInstances.CountDocumentsAsync(
            x =>
                x.FlowInstanceId == assignment.FlowInstanceId &&
                x.AssignmentId == assignment.Id &&
                !x.IsDeleted,
            cancellationToken: ct);
        if (canonicalStepCount != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_REPORT_STEP_OWNERSHIP_DRIFT");
        }

        foreach (var entry in pendingEntries
                     .OrderBy(item => item.LifecycleRevision)
                     .ThenBy(item => item.EntryKey, StringComparer.Ordinal))
        {
            await ProjectReportEntryAsync(sourceReport, assignment, entry, ct);
        }
    }

    public async Task<DynamicFlowRuntimeStateProjectionResult?> ProjectAssignmentCompletionAsync(
        string assignmentId,
        string actorUserId,
        CancellationToken ct = default)
    {
        assignmentId = Required(assignmentId, nameof(assignmentId));
        actorUserId = RequiredObjectId(actorUserId, nameof(actorUserId));
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return null;
        if (!assignment.CompletedAtUtc.HasValue)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_COMPLETION_NOT_COMMITTED");
        }

        var projectionActorUserId = RequiredObjectId(
            assignment.CompletedByUserId ?? actorUserId,
            nameof(assignment.CompletedByUserId));
        var sourceCommandId = $"assignment-completed:{assignment.Id}";
        var requestHash = Hash(string.Join(
            "\n",
            assignment.Id,
            assignment.CompletedAtUtc.Value.ToUniversalTime().ToString("O"),
            assignment.CompletedDate?.ToUniversalTime().ToString("O") ?? string.Empty,
            projectionActorUserId));
        var projectionFingerprint = Hash(string.Join(
            "\n",
            "assignment-completion",
            assignment.Id,
            assignment.CompletedAtUtc.Value.ToUniversalTime().ToString("O"),
            requestHash));

        var result = await ProjectCoreWithRetryAsync(
            assignment,
            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete,
            sourceCommandId,
            sourceCommandId,
            requestHash,
            projectionFingerprint,
            projectionActorUserId,
            sourceReportId: null,
            reportLifecycleEntryKey: null,
            sourceLifecycleRevision: null,
            sourceLifecycleStatus: null,
            sourceLifecycleIsActive: null,
            targetState: DynamicFlowStepStates.Completed,
            reasonCode: "ASSIGNMENT_COMPLETED",
            allowInstanceCompletion: true,
            ct);
        await ProjectSubflowParentTerminalAsync(
            result.FlowInstanceId,
            projectionActorUserId,
            ct);
        return result;
    }

    public async Task<bool> IsAssignmentCompletionProjectionReadyAsync(
        string assignmentId,
        CancellationToken ct = default)
    {
        assignmentId = Required(assignmentId, nameof(assignmentId));
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return true;
        if (!assignment.IsActive)
            return false;

        var steps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == assignment.FlowInstanceId &&
                x.AssignmentId == assignment.Id &&
                !x.IsDeleted)
            .ToListAsync(ct);
        if (steps.Count != 1)
            return false;
        var allSteps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == assignment.FlowInstanceId &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var instance = await _ctx.DynamicFlowInstances
            .Find(x =>
                x.Id == assignment.FlowInstanceId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (instance?.ArchetypeId ==
            DynamicFlowFinalizeTopologyContract.ArchetypeId)
        {
            allSteps = allSteps
                .Where(candidate =>
                    candidate.ExecutionEpoch ==
                        instance.ExecutionEpoch &&
                    candidate.IsCanonicalEpoch != false)
                .ToList();
        }
        var lateJoinCompletionEligible = false;
        if (instance is not null &&
            instance.ArchetypeId ==
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId &&
            instance.State == DynamicFlowInstanceStates.Completed &&
            steps[0].State ==
                DynamicFlowStepStates.CancelledByGateway &&
            assignment.FlowEffectiveStatus ==
                DynamicFlowEffectiveStatuses.Invalidated &&
            steps[0].GatewayInstanceId is not null &&
            steps[0].GatewayVersion is not null &&
            steps[0].ContributionId is not null)
        {
            var gateway = await _ctx.DynamicFlowGatewayInstances
                .Find(item =>
                    item.FlowInstanceId == instance.Id &&
                    item.GatewayInstanceId ==
                        steps[0].GatewayInstanceId &&
                    item.GatewayVersion ==
                        steps[0].GatewayVersion &&
                    item.State ==
                        DynamicFlowGatewayStates.Satisfied)
                .FirstOrDefaultAsync(ct);
            lateJoinCompletionEligible =
                gateway is not null &&
                gateway.CancelledContributionIds.Contains(
                    steps[0].ContributionId,
                    StringComparer.Ordinal);
        }
        if (instance is null ||
            instance.State != DynamicFlowInstanceStates.Active &&
            !lateJoinCompletionEligible)
        {
            return false;
        }
        var materializationItems = await _ctx.DynamicFlowRuntimeOutbox
            .Find(x => x.FlowInstanceId == assignment.FlowInstanceId)
            .ToListAsync(ct);
        if (instance.ArchetypeId ==
            DynamicFlowFinalizeTopologyContract.ArchetypeId)
        {
            var currentStepIds = allSteps
                .Select(candidate => candidate.Id)
                .ToHashSet(StringComparer.Ordinal);
            materializationItems = materializationItems
                .Where(item =>
                    currentStepIds.Contains(item.StepInstanceId) &&
                    item.Operation ==
                        DynamicFlowRuntimeMaterializationOperations
                            .MaterializeEntryAssignment)
                .ToList();
        }
        if (materializationItems.Count != allSteps.Count ||
            materializationItems.Any(item =>
                item.Status != DynamicFlowRuntimeOutboxStatuses.Completed) ||
            allSteps.Any(step =>
            {
                var expectedOperation =
                    string.Equals(
                        instance.ArchetypeId,
                        DynamicFlowReviewLoopTopologyContract.ArchetypeId,
                        StringComparison.Ordinal) &&
                    step.AttemptNo > 1
                        ? DynamicFlowRuntimeMaterializationOperations
                            .MaterializeReviewAttemptAssignment
                        : string.Equals(
                        step.FlowStepId,
                        instance.EntryFlowStepId,
                        StringComparison.Ordinal)
                        ? DynamicFlowRuntimeMaterializationOperations
                            .MaterializeEntryAssignment
                        : string.Equals(
                            instance.ArchetypeId,
                            DynamicFlowParallelForkTopologyContract.ArchetypeId,
                            StringComparison.Ordinal) ||
                        string.Equals(
                            instance.ArchetypeId,
                            DynamicFlowJoinAllTopologyContract.ArchetypeId,
                            StringComparison.Ordinal) ||
                        string.Equals(
                            instance.ArchetypeId,
                            DynamicFlowJoinQuorumTopologyContract.ArchetypeId,
                            StringComparison.Ordinal) ||
                        string.Equals(
                            instance.ArchetypeId,
                            DynamicFlowTypedConditionalTopologyContract.ArchetypeId,
                            StringComparison.Ordinal)
                            ? DynamicFlowRuntimeMaterializationOperations
                                .MaterializeForkBranchAssignment
                            : DynamicFlowRuntimeMaterializationOperations
                                .MaterializeSequentialAssignment;
                return materializationItems.Count(item =>
                           item.StepInstanceId == step.Id &&
                           item.Operation == expectedOperation) != 1;
            }))
        {
            return false;
        }
        var archetypeId = await ResolveStepArchetypeAsync(
            session: null,
            assignment.FlowInstanceId,
            steps[0].Id,
            ct);
        var reports = await _ctx.WorkAssignmentReports
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                !x.IsDeleted)
            .ToListAsync(ct);
        if (reports.Any(report =>
                (report.IsActive &&
                 report.AggregateSnapshotDirty) ||
                !string.IsNullOrWhiteSpace(report.PayloadMutationCommandId)))
        {
            return false;
        }
        var lifecycleEntries = reports
            .SelectMany(report =>
                (report.LifecycleProjectionOutbox ??
                 new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Select(entry => new
                {
                    Report = report,
                    Entry = entry,
                    SourceEventKey = Hash($"{report.Id}\n{entry.EntryKey}")
                }))
            .ToArray();
        if (lifecycleEntries.Any(item =>
                item.Entry.State !=
                WorkReportLifecycleProjectionOutboxStates.Completed) ||
            lifecycleEntries
                .Select(item => item.SourceEventKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != lifecycleEntries.Length)
        {
            return false;
        }
        if (lifecycleEntries.Length > 0)
        {
            var projectedSourceKeys = (await _ctx.DynamicFlowRuntimeCommandReceipts
                    .Find(x =>
                        x.FlowInstanceId == assignment.FlowInstanceId &&
                        x.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                        x.CommandType ==
                        DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle &&
                        x.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
                    .Project(x => x.CommandId)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            if (lifecycleEntries.Any(item =>
                    !projectedSourceKeys.Contains(item.SourceEventKey)))
            {
                return false;
            }
        }
        if (archetypeId is not (
                "FLOW-T01" or
                "FLOW-T02" or
                "FLOW-T03" or
                "FLOW-T04" or
                "FLOW-T05" or
                "FLOW-T06" or
                "FLOW-T07" or
                "FLOW-T08" or
                "FLOW-T12"))
            return false;
        if (archetypeId == "FLOW-T01" && allSteps.Count != 1)
            return false;
        if (archetypeId == DynamicFlowSequentialTopologyContract.ArchetypeId)
        {
            if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash))
            {
                return false;
            }

            try
            {
                var topology = DynamicFlowSequentialTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash);
                if (!string.Equals(
                        instance.TopologySnapshotHash,
                        instance.FlowPayloadHash,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        instance.DefinitionRevision,
                        DynamicFlowSequentialTopologyContract
                            .BuildDefinitionRevision(
                                instance.FlowTemplateVersionId,
                                instance.FlowTemplateVersionNo,
                                instance.FlowPayloadHash),
                        StringComparison.Ordinal) ||
                    !topology.OrderedNodes.Any(node =>
                        string.Equals(
                            node.NodeId,
                            steps[0].FlowStepId,
                            StringComparison.Ordinal)))
                {
                    return false;
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                return false;
            }
        }
        else if (archetypeId ==
                 DynamicFlowReviewLoopTopologyContract.ArchetypeId)
        {
            if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash))
            {
                return false;
            }
            try
            {
                var topology =
                    DynamicFlowReviewLoopTopologyContract.Require(
                        instance.TopologySnapshotJson,
                        instance.TopologySnapshotHash);
                if (instance.TopologySnapshotHash !=
                        instance.FlowPayloadHash ||
                    steps[0].FlowStepId !=
                        topology.ReviewNode.NodeId ||
                    steps[0].ReviewCycleNo != steps[0].AttemptNo ||
                    steps[0].SupersededByStepInstanceId is not null)
                {
                    return false;
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                return false;
            }
        }
        else if (archetypeId ==
                 DynamicFlowFinalizeTopologyContract.ArchetypeId)
        {
            if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash))
            {
                return false;
            }
            try
            {
                var topology =
                    DynamicFlowFinalizeTopologyContract.Require(
                        instance.TopologySnapshotJson,
                        instance.TopologySnapshotHash);
                if (instance.TopologySnapshotHash !=
                        instance.FlowPayloadHash ||
                    instance.DefinitionRevision !=
                    DynamicFlowSequentialTopologyContract
                        .BuildDefinitionRevision(
                            instance.FlowTemplateVersionId,
                            instance.FlowTemplateVersionNo,
                            instance.FlowPayloadHash) ||
                    steps[0].FlowStepId !=
                        topology.EntryNode.NodeId ||
                    steps[0].ExecutionEpoch !=
                        instance.ExecutionEpoch ||
                    steps[0].IsCanonicalEpoch == false)
                {
                    return false;
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                return false;
            }
        }
        else if (archetypeId is
                 DynamicFlowParallelForkTopologyContract.ArchetypeId or
                 DynamicFlowJoinAllTopologyContract.ArchetypeId or
                 DynamicFlowJoinQuorumTopologyContract.ArchetypeId or
                 DynamicFlowTypedConditionalTopologyContract.ArchetypeId)
        {
            if (!DynamicFlowRuntimeEligibilityPolicy.IsP6CandidateCatalog(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash))
            {
                return false;
            }
            try
            {
                var topology = RequireParallelFanOutTopology(instance);
                if (instance.TopologySnapshotHash != instance.FlowPayloadHash ||
                    instance.DefinitionRevision !=
                    DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                        instance.FlowTemplateVersionId,
                        instance.FlowTemplateVersionNo,
                        instance.FlowPayloadHash) ||
                    steps[0].FlowStepId != topology.EntryNode.NodeId &&
                    topology.Branches.All(branch =>
                        branch.Node.NodeId != steps[0].FlowStepId))
                {
                    return false;
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                return false;
            }
        }
        var activePeriods = await _ctx.WorkReportPeriods
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var activePeriodIds = activePeriods
            .Select(period => period.Id)
            .ToHashSet(StringComparer.Ordinal);
        var activeApprovedReports = reports
            .Where(report =>
                activePeriodIds.Contains(report.WorkReportPeriodId) &&
                report.IsCurrent &&
                report.IsActive &&
                report.Status == WorkAssignmentReportStatus.Approved)
            .GroupBy(report => report.WorkReportPeriodId, StringComparer.Ordinal)
            .ToArray();
        if (activePeriods.Count == 0 ||
            activeApprovedReports.Length != activePeriodIds.Count ||
            activeApprovedReports.Any(group =>
                group.Count() != 1 ||
                !(group.Single().LifecycleProjectionOutbox ??
                  new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Any()))
        {
            return false;
        }
        var reportProjection = await ResolveReportProjectionAsync(
            session: null,
            assignment,
            ct);
        var stableStepState = archetypeId is
            "FLOW-T01" or
            "FLOW-T03" or
            "FLOW-T04" or
            "FLOW-T05" or
            "FLOW-T06" or
            "FLOW-T07" or
            "FLOW-T08"
            ? steps[0].State is
                DynamicFlowStepStates.Approved or
                DynamicFlowStepStates.Completed ||
              lateJoinCompletionEligible &&
              steps[0].State ==
                DynamicFlowStepStates.CancelledByGateway
            : steps[0].State == DynamicFlowStepStates.Approved;
        return reportProjection.TargetState == DynamicFlowStepStates.Approved &&
               reportProjection.LifecycleIsActive == true &&
               stableStepState &&
               string.Equals(
                   steps[0].ReportId,
                   reportProjection.CanonicalReportId,
                   StringComparison.Ordinal) &&
               steps[0].ReportLifecycleRevision ==
               reportProjection.LifecycleRevision &&
               string.Equals(
                   steps[0].ReportLifecycleEntryKey,
                   reportProjection.LifecycleEntryKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   steps[0].ReportLifecycleCommandId,
                   reportProjection.LifecycleCommandId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   steps[0].ReportLifecycleStatus,
                   reportProjection.LifecycleStatus,
                   StringComparison.Ordinal) &&
               steps[0].ReportLifecycleIsActive ==
               reportProjection.LifecycleIsActive;
    }

    public async Task<DynamicFlowRuntimeStateReconcileResult> ReconcileInstanceAsync(
        string flowInstanceId,
        bool apply,
        string actorUserId,
        CancellationToken ct = default)
    {
        flowInstanceId = RequiredObjectId(flowInstanceId, nameof(flowInstanceId));
        actorUserId = RequiredObjectId(actorUserId, nameof(actorUserId));
        var instance = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == flowInstanceId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
        var steps = await _ctx.DynamicFlowStepInstances
            .Find(x => x.FlowInstanceId == instance.Id && !x.IsDeleted)
            .ToListAsync(ct);
        var assignmentIds = steps
            .Where(step => !string.IsNullOrWhiteSpace(step.AssignmentId))
            .Select(step => step.AssignmentId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var assignments = assignmentIds.Count == 0
            ? new List<WorkAssignment>()
            : await _ctx.WorkAssignments
                .Find(x => assignmentIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct);
        if (steps.Count == 0 ||
            steps.Any(step => string.IsNullOrWhiteSpace(step.AssignmentId)) ||
            steps.GroupBy(step => step.AssignmentId!, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            assignments.Count != assignmentIds.Count ||
            assignments.Any(assignment =>
                assignment.FlowInstanceId != instance.Id))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_OWNERSHIP_DRIFT");
        }
        var internalCompletionEligible =
            steps.Count == 1 &&
            await IsT01SoleStepAsync(
                session: null,
                instance.Id,
                steps[0].Id,
                steps,
                ct);
        var assignmentById = assignments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var reports = assignmentIds.Count == 0
            ? new List<WorkAssignmentReport>()
            : await _ctx.WorkAssignmentReports
                .Find(x =>
                    assignmentIds.Contains(x.WorkAssignmentId) &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        var reportReceipts = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x =>
                x.FlowInstanceId == instance.Id &&
                x.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle)
            .ToListAsync(ct);
        var completionReceipts = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x =>
                x.FlowInstanceId == instance.Id &&
                x.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                x.CommandType ==
                DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete)
            .ToListAsync(ct);
        var completionProjectionDebt = assignments
            .Where(assignment => assignment.CompletedAtUtc.HasValue)
            .Where(assignment =>
            {
                var sourceCommandId =
                    $"assignment-completed:{assignment.Id}";
                return !completionReceipts.Any(receipt =>
                    receipt.CommandId == sourceCommandId &&
                    receipt.Status ==
                    DynamicFlowRuntimeCommandStatuses.Succeeded &&
                    receipt.ResultSnapshot is not null &&
                    receipt.ResultSnapshotHash ==
                    Hash(receipt.ResultSnapshot.ToJson()) &&
                    receipt.ResultSnapshot.TryGetValue(
                        "assignmentId",
                        out var assignmentIdValue) &&
                    assignmentIdValue.IsString &&
                    assignmentIdValue.AsString == assignment.Id);
            })
            .OrderBy(assignment => assignment.Id, StringComparer.Ordinal)
            .ToArray();
        var projectedSourceKeys = reportReceipts
            .Where(receipt =>
                receipt.Status == DynamicFlowRuntimeCommandStatuses.Succeeded)
            .Select(receipt => receipt.CommandId)
            .ToHashSet(StringComparer.Ordinal);
        var pendingReportIds = reports
            .Where(report =>
                (report.LifecycleProjectionOutbox ??
                 new List<WorkReportLifecycleProjectionOutboxEntry>())
                .Any(entry =>
                    entry.State != WorkReportLifecycleProjectionOutboxStates.Completed))
            .Select(report => report.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
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
        var blockedProjectionAssignmentIds = reports
            .Where(report =>
                pendingReportIds.Contains(report.Id, StringComparer.Ordinal) ||
                unprojectedReportIds.Contains(report.Id, StringComparer.Ordinal))
            .Select(report => report.WorkAssignmentId)
            .ToHashSet(StringComparer.Ordinal);
        var quorumImpossible = await DetectJoinQuorumImpossibleAsync(
            instance,
            steps,
            assignments,
            ct);
        if (apply && quorumImpossible is not null)
        {
            await MarkJoinQuorumImpossibleAsync(
                instance.Id,
                actorUserId,
                ct);
            var impossibleDriftAssignments = quorumImpossible
                .UnavailableAssignmentIds
                .Concat(blockedProjectionAssignmentIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return new DynamicFlowRuntimeStateReconcileResult(
                instance.Id,
                true,
                assignments.Count,
                1,
                1,
                impossibleDriftAssignments,
                pendingReportIds,
                unprojectedReportIds);
        }
        var drift = new List<(
            WorkAssignment Assignment,
            DynamicFlowStepInstance Step,
            string Target,
            bool StateDrift,
            bool MetadataDrift,
            string Fingerprint)>();
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.AssignmentId) ||
                !assignmentById.TryGetValue(step.AssignmentId, out var assignment))
            {
                continue;
            }
            if (blockedProjectionAssignmentIds.Contains(assignment.Id))
                continue;

            var reportProjection = await ResolveReportProjectionAsync(null, assignment, ct);
            var target = assignment.CompletedAtUtc.HasValue &&
                         internalCompletionEligible
                ? DynamicFlowStepStates.Completed
                : reportProjection.TargetState;
            if (step.State == DynamicFlowStepStates.Completed)
            {
                // Keep reconcile preview aligned with ProjectCoreWithRetryAsync:
                // delayed lifecycle evidence may repair provenance, but it cannot
                // reopen a durable terminal step (including a forwarded T03 node).
                target = DynamicFlowStepStates.Completed;
            }
            else if (target == DynamicFlowStepStates.Assigned &&
                step.State is DynamicFlowStepStates.InProgress
                    or DynamicFlowStepStates.Submitted
                    or DynamicFlowStepStates.Returned
                    or DynamicFlowStepStates.Approved
                    or DynamicFlowStepStates.Completed)
            {
                // Missing/no-report evidence is insufficient authority to regress a durable
                // runtime state. Lifecycle commands may only move through the frozen matrix.
                target = step.State;
            }
            var stateDrift = !string.Equals(step.State, target, StringComparison.Ordinal);
            var metadataDrift =
                !string.Equals(
                    step.ReportId,
                    reportProjection.CanonicalReportId,
                    StringComparison.Ordinal) ||
                step.ReportLifecycleRevision != reportProjection.LifecycleRevision ||
                !string.Equals(
                    step.ReportLifecycleEntryKey,
                    reportProjection.LifecycleEntryKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    step.ReportLifecycleCommandId,
                    reportProjection.LifecycleCommandId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    step.ReportLifecycleStatus,
                    reportProjection.LifecycleStatus,
                    StringComparison.Ordinal) ||
                step.ReportLifecycleIsActive != reportProjection.LifecycleIsActive;
            if (!stateDrift && !metadataDrift)
                continue;
            var fingerprint = Hash(string.Join(
                "\n",
                assignment.Id,
                target,
                reportProjection.Fingerprint,
                step.Revision,
                step.ReportId ?? string.Empty,
                step.ReportLifecycleRevision?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                step.ReportLifecycleEntryKey ?? string.Empty,
                step.ReportLifecycleCommandId ?? string.Empty,
                step.ReportLifecycleStatus ?? string.Empty,
                step.ReportLifecycleIsActive?.ToString() ?? string.Empty));
            drift.Add((
                assignment,
                step,
                target,
                stateDrift,
                metadataDrift,
                fingerprint));
        }

        var projected = 0;
        var projectedCompletionAssignmentIds =
            new HashSet<string>(StringComparer.Ordinal);
        if (apply)
        {
            foreach (var item in drift)
            {
                if (item.Target == DynamicFlowStepStates.Completed && item.StateDrift)
                {
                    await ProjectAssignmentCompletionAsync(item.Assignment.Id, actorUserId, ct);
                    projectedCompletionAssignmentIds.Add(item.Assignment.Id);
                }
                if (item.Target != DynamicFlowStepStates.Completed || item.MetadataDrift)
                {
                    var sourceCommandId =
                        $"state-reconcile:{item.Assignment.Id}:{item.Fingerprint[..16]}";
                    await ProjectCoreWithRetryAsync(
                        item.Assignment,
                        DynamicFlowRuntimeStateProjectionOperations.StateReconcile,
                        sourceCommandId,
                        sourceCommandId,
                        Hash($"{sourceCommandId}\n{item.Target}"),
                        item.Fingerprint,
                        actorUserId,
                        sourceReportId: null,
                        reportLifecycleEntryKey: null,
                        sourceLifecycleRevision: null,
                        sourceLifecycleStatus: null,
                        sourceLifecycleIsActive: null,
                        targetState: item.Target,
                        reasonCode: "STATE_RECONCILE",
                        allowInstanceCompletion: false,
                        ct);
                }
                projected++;
            }

            foreach (var assignment in completionProjectionDebt)
            {
                if (projectedCompletionAssignmentIds.Add(assignment.Id))
                {
                    await ProjectAssignmentCompletionAsync(
                        assignment.Id,
                        actorUserId,
                        ct);
                    projected++;
                }
            }

            var refreshedInstance = await _ctx.DynamicFlowInstances
                .Find(x => x.Id == instance.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("DYNAMIC_FLOW_INSTANCE_NOT_FOUND");
            var refreshedSteps = await _ctx.DynamicFlowStepInstances
                .Find(x => x.FlowInstanceId == instance.Id && !x.IsDeleted)
                .ToListAsync(ct);
            if (internalCompletionEligible &&
                refreshedInstance.State == DynamicFlowInstanceStates.Active &&
                refreshedSteps.Count > 0 &&
                DynamicFlowSupplementalTopologyContract.CompletionSatisfied(
                    refreshedSteps))
            {
                var repairAssignment = assignments
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .First();
                var fingerprint = Hash(
                    $"{instance.Id}\ninstance-completed\n{refreshedInstance.Revision}");
                var sourceCommandId =
                    $"state-reconcile-instance:{instance.Id}:{fingerprint[..16]}";
                await ProjectCoreWithRetryAsync(
                    repairAssignment,
                    DynamicFlowRuntimeStateProjectionOperations.StateReconcile,
                    sourceCommandId,
                    sourceCommandId,
                    Hash($"{sourceCommandId}\n{DynamicFlowInstanceStates.Completed}"),
                    fingerprint,
                    actorUserId,
                    sourceReportId: null,
                    reportLifecycleEntryKey: null,
                    sourceLifecycleRevision: null,
                    sourceLifecycleStatus: null,
                    sourceLifecycleIsActive: null,
                    targetState: DynamicFlowStepStates.Completed,
                    reasonCode: "STATE_RECONCILE_INSTANCE_COMPLETION",
                    allowInstanceCompletion: true,
                    ct);
                projected++;
            }
        }

        var instanceCompletionDrift =
            internalCompletionEligible &&
            instance.State == DynamicFlowInstanceStates.Active &&
            steps.Count > 0 &&
            DynamicFlowSupplementalTopologyContract.CompletionSatisfied(steps);
        var driftAssignmentIds = drift
            .Select(item => item.Assignment.Id)
            .Concat(completionProjectionDebt.Select(item => item.Id))
            .Concat(blockedProjectionAssignmentIds)
            .Concat(
                quorumImpossible?.UnavailableAssignmentIds ??
                Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new DynamicFlowRuntimeStateReconcileResult(
            instance.Id,
            apply,
            assignments.Count,
            driftAssignmentIds.Length +
            (instanceCompletionDrift ? 1 : 0) +
            (quorumImpossible is not null ? 1 : 0),
            projected,
            driftAssignmentIds,
            pendingReportIds,
            unprojectedReportIds);
    }

    private sealed record JoinQuorumImpossibleProjection(
        DynamicFlowGatewayInstance Gateway,
        IReadOnlyList<string> UnavailableContributionIds,
        IReadOnlyList<string> UnavailableAssignmentIds);

    private async Task<JoinQuorumImpossibleProjection?>
        DetectJoinQuorumImpossibleAsync(
            DynamicFlowInstance instance,
            IReadOnlyList<DynamicFlowStepInstance> steps,
            IReadOnlyList<WorkAssignment> assignments,
            CancellationToken ct)
    {
        if (instance.ArchetypeId !=
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId ||
            instance.State != DynamicFlowInstanceStates.Active)
        {
            return null;
        }
        var gateways = await _ctx.DynamicFlowGatewayInstances
            .Find(gateway => gateway.FlowInstanceId == instance.Id)
            .ToListAsync(ct);
        if (gateways.Count == 0)
            return null;
        if (gateways.Count != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_JOIN_LEDGER_DRIFT");
        }
        var gateway = gateways[0];
        if (gateway.State != DynamicFlowGatewayStates.Collecting)
            return null;
        var contributions = await _ctx.DynamicFlowGatewayContributions
            .Find(item =>
                item.FlowInstanceId == instance.Id &&
                item.GatewayInstanceId == gateway.GatewayInstanceId &&
                item.GatewayVersion == gateway.GatewayVersion)
            .ToListAsync(ct);
        return EvaluateJoinQuorumImpossible(
            gateway,
            steps,
            assignments,
            contributions);
    }

    private static JoinQuorumImpossibleProjection?
        EvaluateJoinQuorumImpossible(
            DynamicFlowGatewayInstance gateway,
            IReadOnlyList<DynamicFlowStepInstance> steps,
            IReadOnlyList<WorkAssignment> assignments,
            IReadOnlyList<DynamicFlowGatewayContribution> contributions)
    {
        var expected = gateway.ExpectedContributionIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var arrived = contributions
            .Where(item =>
                item.EffectiveStatus ==
                DynamicFlowGatewayContributionOutcomes.Effective)
            .Select(item => item.ContributionId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (gateway.GatewayKind is not (
                DynamicFlowGatewayKinds.JoinAny or
                DynamicFlowGatewayKinds.JoinNOfM) ||
            expected.Length == 0 ||
            gateway.RequiredContributionCount < 1 ||
            gateway.RequiredContributionCount > expected.Length ||
            expected.Distinct(StringComparer.Ordinal).Count() !=
                expected.Length ||
            arrived.Distinct(StringComparer.Ordinal).Count() !=
                arrived.Length ||
            arrived.Any(value =>
                !expected.Contains(value, StringComparer.Ordinal)) ||
            !gateway.ArrivedContributionIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(arrived, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_JOIN_LEDGER_DRIFT");
        }
        var assignmentById = assignments
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var stepByContribution = steps
            .Where(item =>
                item.GatewayInstanceId == gateway.GatewayInstanceId &&
                item.GatewayVersion == gateway.GatewayVersion &&
                item.ContributionId is not null)
            .GroupBy(item => item.ContributionId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        if (expected.Any(contributionId =>
                !stepByContribution.TryGetValue(
                    contributionId,
                    out var matches) ||
                matches.Length != 1 ||
                string.IsNullOrWhiteSpace(matches[0].AssignmentId) ||
                !assignmentById.ContainsKey(matches[0].AssignmentId!)))
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_JOIN_LEDGER_DRIFT");
        }
        var arrivedSet = arrived.ToHashSet(StringComparer.Ordinal);
        var stillPossible = expected
            .Where(contributionId =>
            {
                if (arrivedSet.Contains(contributionId))
                    return false;
                var step = stepByContribution[contributionId][0];
                var assignment = assignmentById[step.AssignmentId!];
                return assignment.IsActive &&
                       assignment.FlowEffectiveStatus ==
                       DynamicFlowEffectiveStatuses.Effective &&
                       step.State is not (
                           DynamicFlowStepStates.CancelledByGateway or
                           DynamicFlowStepStates.Failed or
                           DynamicFlowStepStates.Terminated);
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!DynamicFlowJoinQuorumTopologyContract.IsQuorumImpossible(
                gateway.RequiredContributionCount,
                arrived,
                stillPossible))
        {
            return null;
        }
        var possible = arrived
            .Concat(stillPossible)
            .ToHashSet(StringComparer.Ordinal);
        var unavailable = expected
            .Where(value => !possible.Contains(value))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var unavailableAssignments = unavailable
            .Select(value => stepByContribution[value][0].AssignmentId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new JoinQuorumImpossibleProjection(
            gateway,
            unavailable,
            unavailableAssignments);
    }

    private Task MarkJoinQuorumImpossibleAsync(
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var instance = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        item => item.Id == flowInstanceId && !item.IsDeleted)
                    .SingleAsync(transactionCt);
                var gateways = await _ctx.DynamicFlowGatewayInstances
                    .Find(
                        session,
                        gateway =>
                            gateway.FlowInstanceId == flowInstanceId)
                    .ToListAsync(transactionCt);
                if (instance.State == DynamicFlowInstanceStates.Failed &&
                    gateways.Count == 1 &&
                    gateways[0].State ==
                        DynamicFlowGatewayStates.Impossible)
                {
                    return;
                }
                if (instance.ArchetypeId !=
                        DynamicFlowJoinQuorumTopologyContract.ArchetypeId ||
                    instance.State != DynamicFlowInstanceStates.Active ||
                    gateways.Count != 1 ||
                    gateways[0].State !=
                        DynamicFlowGatewayStates.Collecting)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_JOIN_LEDGER_DRIFT");
                }
                var gateway = gateways[0];
                var steps = await _ctx.DynamicFlowStepInstances
                    .Find(
                        session,
                        item =>
                            item.FlowInstanceId == flowInstanceId &&
                            !item.IsDeleted)
                    .ToListAsync(transactionCt);
                var assignmentIds = steps
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.AssignmentId))
                    .Select(item => item.AssignmentId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var assignments = await _ctx.WorkAssignments
                    .Find(
                        session,
                        item =>
                            assignmentIds.Contains(item.Id) &&
                            !item.IsDeleted)
                    .ToListAsync(transactionCt);
                var contributions = await _ctx
                    .DynamicFlowGatewayContributions
                    .Find(
                        session,
                        item =>
                            item.FlowInstanceId == flowInstanceId &&
                            item.GatewayInstanceId ==
                                gateway.GatewayInstanceId &&
                            item.GatewayVersion ==
                                gateway.GatewayVersion)
                    .ToListAsync(transactionCt);
                var impossible = EvaluateJoinQuorumImpossible(
                    gateway,
                    steps,
                    assignments,
                    contributions)
                    ?? throw new InvalidOperationException(
                        "DYNAMIC_FLOW_JOIN_QUORUM_STILL_POSSIBLE");
                var now = DateTime.UtcNow;
                var commandId =
                    $"join-quorum-impossible:{gateway.GatewayInstanceId}:{gateway.GatewayVersion}";
                var eventId = DynamicFlowJoinQuorumTopologyContract
                    .BuildImpossibleEventId(
                        gateway.GatewayInstanceId,
                        gateway.GatewayVersion);
                var instanceEventId = StableObjectId(
                    $"{instance.Id}\n{gateway.GatewayInstanceId}\n{gateway.GatewayVersion}\ninstance-failed");
                var eventIds = new[] { eventId, instanceEventId };
                var result = new BsonDocument
                {
                    { "flowInstanceId", instance.Id },
                    {
                        "gatewayInstanceId",
                        gateway.GatewayInstanceId
                    },
                    {
                        "gatewayVersion",
                        gateway.GatewayVersion
                    },
                    { "reasonCode", "QUORUM_IMPOSSIBLE" },
                    {
                        "unavailableContributionIds",
                        new BsonArray(
                            impossible.UnavailableContributionIds)
                    },
                    { "eventIds", new BsonArray(eventIds) }
                };
                var receiptId = StableObjectId(
                    $"{DynamicFlowCommandScopeKinds.Instance}\n{instance.Id}\n{DynamicFlowRuntimeStateProjectionOperations.StateReconcile}\n{commandId}");
                await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeCommandReceipt
                    {
                        Id = receiptId,
                        ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                        ScopeId = instance.Id,
                        WorkId = instance.WorkId,
                        FlowTemplateVersionId =
                            instance.FlowTemplateVersionId,
                        FlowInstanceId = instance.Id,
                        CommandType =
                            DynamicFlowRuntimeStateProjectionOperations
                                .StateReconcile,
                        CommandId = commandId,
                        RequestHash = Hash(commandId),
                        CommandIdentityHash = Hash(
                            $"{instance.Id}\n{commandId}"),
                        SnapshotToken =
                            $"{instance.Revision}:{gateway.Revision}",
                        ExpectedRevision = instance.Revision,
                        Status =
                            DynamicFlowRuntimeCommandStatuses.Succeeded,
                        ResultSnapshot = result,
                        ResultSnapshotHash = Hash(result.ToJson()),
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        UpdatedByUserId = actorUserId,
                        CompletedAtUtc = now
                    },
                    cancellationToken: transactionCt);
                var gatewayUpdate = await _ctx
                    .DynamicFlowGatewayInstances.UpdateOneAsync(
                        session,
                        item =>
                            item.Id == gateway.Id &&
                            item.Revision == gateway.Revision &&
                            item.State ==
                                DynamicFlowGatewayStates.Collecting,
                        Builders<DynamicFlowGatewayInstance>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowGatewayStates.Impossible)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Set(
                                item => item.UpdatedByUserId,
                                actorUserId)
                            .Inc(item => item.Revision, 1),
                        cancellationToken: transactionCt);
                if (gatewayUpdate.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_REVISION_CONFLICT");
                }
                DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                    instance.State,
                    DynamicFlowInstanceStates.Failed);
                var instanceUpdate = await _ctx
                    .DynamicFlowInstances.UpdateOneAsync(
                        session,
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            instance.Id,
                            instance.Revision,
                            instance.State),
                        Builders<DynamicFlowInstance>.Update
                            .Set(
                                item => item.State,
                                DynamicFlowInstanceStates.Failed)
                            .Set(
                                item => item.LastErrorCode,
                                "DYNAMIC_FLOW_JOIN_QUORUM_IMPOSSIBLE")
                            .Set(item => item.UpdatedAtUtc, now)
                            .Set(
                                item => item.UpdatedByUserId,
                                actorUserId)
                            .Inc(item => item.Revision, 1)
                            .Inc(item => item.NextEventSequence, 2),
                        cancellationToken: transactionCt);
                if (instanceUpdate.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_REVISION_CONFLICT");
                }
                var affectedRefs = impossible
                    .UnavailableContributionIds
                    .Select(value => $"contribution:{value}")
                    .Prepend($"gateway:{gateway.GatewayInstanceId}")
                    .ToList();
                var visibleUnits = steps
                    .Select(item => item.TargetUnitId)
                    .Append(instance.IssuerUnitId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
                var payload = new BsonDocument
                {
                    {
                        "gatewayInstanceId",
                        gateway.GatewayInstanceId
                    },
                    {
                        "gatewayVersion",
                        gateway.GatewayVersion
                    },
                    {
                        "requiredContributionCount",
                        gateway.RequiredContributionCount
                    },
                    {
                        "arrivedContributionIds",
                        new BsonArray(
                            gateway.ArrivedContributionIds)
                    },
                    {
                        "unavailableContributionIds",
                        new BsonArray(
                            impossible.UnavailableContributionIds)
                    },
                    {
                        "fromState",
                        DynamicFlowGatewayStates.Collecting
                    },
                    {
                        "toState",
                        DynamicFlowGatewayStates.Impossible
                    }
                };
                var failurePayload = new BsonDocument
                {
                    {
                        "gatewayInstanceId",
                        gateway.GatewayInstanceId
                    },
                    {
                        "fromState",
                        DynamicFlowInstanceStates.Active
                    },
                    {
                        "toState",
                        DynamicFlowInstanceStates.Failed
                    },
                    { "reasonCode", "QUORUM_IMPOSSIBLE" }
                };
                await _ctx.DynamicFlowRuntimeEvents.InsertManyAsync(
                    session,
                    new[]
                    {
                        new DynamicFlowRuntimeEvent
                        {
                            Id = eventId,
                            FlowInstanceId = instance.Id,
                            ExecutionEpoch = instance.ExecutionEpoch,
                            GatewayInstanceId =
                                gateway.GatewayInstanceId,
                            GatewayVersion = gateway.GatewayVersion,
                            Sequence = instance.NextEventSequence,
                            EventType =
                                DynamicFlowRuntimeStateProjectionEventTypes
                                    .JoinQuorumImpossible,
                            CommandId = commandId,
                            CorrelationId = commandId,
                            SourceEventKey = commandId,
                            FromState =
                                DynamicFlowGatewayStates.Collecting,
                            ToState =
                                DynamicFlowGatewayStates.Impossible,
                            FromRevision = gateway.Revision,
                            ToRevision = gateway.Revision + 1,
                            ReasonCode = "QUORUM_IMPOSSIBLE",
                            AffectedRefs = affectedRefs,
                            ActorUserId = actorUserId,
                            VisibleUnitIds = visibleUnits,
                            Payload = payload,
                            PayloadHash = Hash(payload.ToJson()),
                            OccurredAtUtc = now
                        },
                        new DynamicFlowRuntimeEvent
                        {
                            Id = instanceEventId,
                            FlowInstanceId = instance.Id,
                            ExecutionEpoch = instance.ExecutionEpoch,
                            Sequence =
                                instance.NextEventSequence + 1,
                            EventType =
                                DynamicFlowRuntimeStateProjectionEventTypes
                                    .InstanceFailed,
                            CommandId = commandId,
                            CorrelationId = commandId,
                            SourceEventKey = commandId,
                            FromState =
                                DynamicFlowInstanceStates.Active,
                            ToState =
                                DynamicFlowInstanceStates.Failed,
                            FromRevision = instance.Revision,
                            ToRevision = instance.Revision + 1,
                            ReasonCode = "QUORUM_IMPOSSIBLE",
                            AffectedRefs = affectedRefs,
                            ActorUserId = actorUserId,
                            VisibleUnitIds = visibleUnits,
                            Payload = failurePayload,
                            PayloadHash =
                                Hash(failurePayload.ToJson()),
                            OccurredAtUtc = now
                        }
                    },
                    cancellationToken: transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    instance.WorkId,
                    transactionCt);
            },
            ct);

    private async Task ProjectReportEntryAsync(
        WorkAssignmentReport sourceReport,
        WorkAssignment assignment,
        WorkReportLifecycleProjectionOutboxEntry entry,
        CancellationToken ct)
    {
        var lifecycleEntryKey = Required(entry.EntryKey, nameof(entry.EntryKey));
        var sourceEventKey = Hash($"{sourceReport.Id}\n{lifecycleEntryKey}");
        var sourceCommandId = Required(entry.CommandId, nameof(entry.CommandId));
        var actorUserId = ResolveActorUserId(sourceReport, entry);
        var requestHash = Hash(string.Join(
            "\n",
            sourceReport.Id,
            sourceReport.WorkAssignmentId,
            sourceReport.WorkReportPeriodId,
            lifecycleEntryKey,
            sourceCommandId,
            entry.LifecycleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.Operation?.Trim().ToUpperInvariant(),
            entry.FromStatus?.Trim().ToUpperInvariant(),
            entry.ToStatus?.Trim().ToUpperInvariant(),
            entry.FromIsActive ? "1" : "0",
            entry.ToIsActive ? "1" : "0",
            actorUserId));
        var reportProjection = await ResolveReportProjectionAsync(
            session: null,
            assignment,
            ct,
            sourceReport.Id,
            entry.LifecycleRevision,
            entry.ToStatus,
            entry.ToIsActive,
            lifecycleEntryKey,
            sourceCommandId,
            entry.Operation);

        await ProjectCoreWithRetryAsync(
            assignment,
            DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle,
            sourceCommandId,
            sourceEventKey,
            requestHash,
            reportProjection.Fingerprint,
            actorUserId,
            sourceReport.Id,
            lifecycleEntryKey,
            entry.LifecycleRevision,
            entry.ToStatus?.Trim().ToUpperInvariant(),
            entry.ToIsActive,
            reportProjection.TargetState,
            $"REPORT_LIFECYCLE_{NormalizeReason(entry.Operation)}",
            allowInstanceCompletion: false,
            ct);

        if (reportProjection.TargetState == DynamicFlowStepStates.Returned)
        {
            await EnsureReviewLoopAttemptAsync(
                sourceReport,
                assignment,
                sourceEventKey,
                requestHash,
                actorUserId,
                ct);
        }
        await ProjectSubflowParentTerminalAsync(
            assignment.FlowInstanceId!,
            actorUserId,
            ct);
    }

    private async Task EnsureReviewLoopAttemptAsync(
        WorkAssignmentReport sourceReport,
        WorkAssignment assignment,
        string sourceEventKey,
        string requestHash,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            return;

        var instanceSnapshot = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == assignment.FlowInstanceId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (instanceSnapshot is null ||
            instanceSnapshot.ArchetypeId !=
            DynamicFlowReviewLoopTopologyContract.ArchetypeId)
        {
            return;
        }

        var receiptId =
            DynamicFlowReviewLoopTopologyContract.BuildAdvanceReceiptId(
                instanceSnapshot.Id,
                sourceEventKey);
        for (var attempt = 1; attempt <= MaxRaceAttempts; attempt++)
        {
            if (await _ctx.DynamicFlowRuntimeCommandReceipts.CountDocumentsAsync(
                    candidate =>
                        candidate.Id == receiptId &&
                        candidate.ScopeKind ==
                        DynamicFlowCommandScopeKinds.Instance &&
                        candidate.ScopeId == instanceSnapshot.Id &&
                        candidate.CommandType ==
                        DynamicFlowReviewLoopTopologyContract
                            .ReviewAdvanceCommand &&
                        candidate.CommandId == sourceEventKey &&
                        candidate.RequestHash == requestHash &&
                        candidate.Status ==
                        DynamicFlowRuntimeCommandStatuses.Succeeded,
                    cancellationToken: ct) == 1)
            {
                return;
            }

            try
            {
                await _transactions.ExecuteAsync(
                    async (session, transactionCt) =>
                    {
                        var existing = await _ctx
                            .DynamicFlowRuntimeCommandReceipts
                            .Find(session, candidate =>
                                candidate.Id == receiptId)
                            .FirstOrDefaultAsync(transactionCt);
                        if (existing is not null)
                        {
                            if (existing.ScopeKind !=
                                    DynamicFlowCommandScopeKinds.Instance ||
                                existing.ScopeId != instanceSnapshot.Id ||
                                existing.CommandType !=
                                DynamicFlowReviewLoopTopologyContract
                                    .ReviewAdvanceCommand ||
                                existing.CommandId != sourceEventKey ||
                                existing.RequestHash != requestHash ||
                                existing.Status !=
                                DynamicFlowRuntimeCommandStatuses.Succeeded)
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_REVIEW_RETURN_REPLAY_CONFLICT");
                            }
                            return;
                        }

                        var instance = await _ctx.DynamicFlowInstances
                            .Find(session, candidate =>
                                candidate.Id == instanceSnapshot.Id &&
                                !candidate.IsDeleted)
                            .SingleAsync(transactionCt);
                        var currentStep = await _ctx.DynamicFlowStepInstances
                            .Find(session, candidate =>
                                candidate.FlowInstanceId == instance.Id &&
                                candidate.AssignmentId == assignment.Id &&
                                !candidate.IsDeleted)
                            .SingleAsync(transactionCt);
                        if (instance.State != DynamicFlowInstanceStates.Active ||
                            currentStep.State !=
                            DynamicFlowStepStates.Returned ||
                            currentStep.AssignmentId != assignment.Id ||
                            currentStep.SupersededByStepInstanceId is not null)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_REVIEW_RETURN_STALE_ATTEMPT");
                        }

                        var topology =
                            DynamicFlowReviewLoopTopologyContract.Require(
                                instance.TopologySnapshotJson,
                                instance.TopologySnapshotHash);
                        if (instance.TopologySnapshotHash !=
                                instance.FlowPayloadHash ||
                            instance.DefinitionRevision !=
                            DynamicFlowSequentialTopologyContract
                                .BuildDefinitionRevision(
                                    instance.FlowTemplateVersionId,
                                    instance.FlowTemplateVersionNo,
                                    instance.FlowPayloadHash) ||
                            currentStep.FlowStepId !=
                                topology.ReviewNode.NodeId ||
                            currentStep.AttemptNo < 1 ||
                            currentStep.ReviewCycleNo !=
                                currentStep.AttemptNo ||
                            currentStep.BranchId !=
                            DynamicFlowReviewLoopTopologyContract
                                .BuildBranchId(
                                    instance.Id,
                                    instance.ExecutionEpoch,
                                    currentStep.TargetUnitId) ||
                            currentStep.Id !=
                            DynamicFlowReviewLoopTopologyContract
                                .BuildStepInstanceId(
                                    instance.Id,
                                    instance.ExecutionEpoch,
                                    topology.ReviewNode.NodeId,
                                    currentStep.BranchId,
                                    currentStep.AttemptNo) ||
                            currentStep.AssignmentId !=
                            DynamicFlowReviewLoopTopologyContract
                                .BuildAssignmentId(currentStep.Id))
                        {
                            throw new InvalidOperationException(
                                DynamicFlowReviewLoopTopologyContract
                                    .LineageDrift);
                        }

                        var now = DateTime.UtcNow;
                        var eventSequence = instance.NextEventSequence;
                        var eventId =
                            DynamicFlowReviewLoopTopologyContract
                                .BuildAdvanceEventId(
                                    instance.Id,
                                    sourceEventKey);
                        var exhausted =
                            currentStep.ReviewCycleNo >=
                            topology.MaxReviewCycles;
                        if (exhausted)
                        {
                            var exhaustedResult = new BsonDocument
                            {
                                { "flowInstanceId", instance.Id },
                                { "status", "FAILED" },
                                {
                                    "errorCode",
                                    DynamicFlowReviewLoopTopologyContract
                                        .CycleExhausted
                                },
                                { "reviewCycleNo", currentStep.ReviewCycleNo },
                                { "maxReviewCycles", topology.MaxReviewCycles },
                                { "sourceReportId", sourceReport.Id },
                                { "eventId", eventId }
                            };
                            var exhaustedPayload = new BsonDocument
                            {
                                { "stepInstanceId", currentStep.Id },
                                { "assignmentId", assignment.Id },
                                { "sourceReportId", sourceReport.Id },
                                { "reviewCycleNo", currentStep.ReviewCycleNo },
                                { "maxReviewCycles", topology.MaxReviewCycles },
                                {
                                    "reasonCode",
                                    DynamicFlowReviewLoopTopologyContract
                                        .CycleExhausted
                                }
                            };
                            await _ctx.DynamicFlowRuntimeCommandReceipts
                                .InsertOneAsync(
                                    session,
                                    BuildReviewAdvanceReceipt(
                                        receiptId,
                                        instance,
                                        sourceEventKey,
                                        requestHash,
                                        actorUserId,
                                        exhaustedResult,
                                        now),
                                    cancellationToken: transactionCt);
                            await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                                session,
                                new DynamicFlowRuntimeEvent
                                {
                                    Id = eventId,
                                    FlowInstanceId = instance.Id,
                                    StepInstanceId = currentStep.Id,
                                    ExecutionEpoch =
                                        currentStep.ExecutionEpoch,
                                    BranchId = currentStep.BranchId,
                                    AttemptNo = currentStep.AttemptNo,
                                    Sequence = eventSequence,
                                    EventType =
                                        DynamicFlowReviewLoopTopologyContract
                                            .ReviewCycleExhaustedEvent,
                                    CommandId = sourceEventKey,
                                    CorrelationId = sourceEventKey,
                                    SourceEventKey = sourceEventKey,
                                    FromState = instance.State,
                                    ToState =
                                        DynamicFlowInstanceStates.Failed,
                                    FromRevision = instance.Revision,
                                    ToRevision = instance.Revision + 1,
                                    ReasonCode =
                                        DynamicFlowReviewLoopTopologyContract
                                            .CycleExhausted,
                                    AffectedRefs =
                                    [
                                        $"step:{currentStep.Id}",
                                        $"assignment:{assignment.Id}",
                                        $"report:{sourceReport.Id}"
                                    ],
                                    ActorUserId = actorUserId,
                                    VisibleUnitIds = new[]
                                        {
                                            currentStep.TargetUnitId,
                                            instance.IssuerUnitId
                                        }
                                        .Distinct(StringComparer.Ordinal)
                                        .OrderBy(
                                            value => value,
                                            StringComparer.Ordinal)
                                        .ToList(),
                                    Payload = exhaustedPayload,
                                    PayloadHash =
                                        Hash(exhaustedPayload.ToJson()),
                                    OccurredAtUtc = now
                                },
                                cancellationToken: transactionCt);
                            var terminalAssignment = await _ctx
                                .WorkAssignments.UpdateOneAsync(
                                    session,
                                    candidate =>
                                        candidate.Id == assignment.Id &&
                                        candidate.FlowInstanceId ==
                                        instance.Id &&
                                        candidate.FlowEffectiveStatus ==
                                        DynamicFlowEffectiveStatuses
                                            .Effective &&
                                        !candidate.IsDeleted,
                                    Builders<WorkAssignment>.Update
                                        .Set(
                                            candidate =>
                                                candidate.FlowEffectiveStatus,
                                            DynamicFlowEffectiveStatuses
                                                .Invalidated)
                                        .Set(
                                            candidate =>
                                                candidate
                                                    .InvalidatedByFlowEventId,
                                            eventId)
                                        .Set(
                                            candidate =>
                                                candidate.UpdatedAtUtc,
                                            now)
                                        .Set(
                                            candidate =>
                                                candidate.UpdatedByUserId,
                                            actorUserId),
                                    cancellationToken: transactionCt);
                            if (terminalAssignment.ModifiedCount != 1)
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_REVIEW_RETURN_ASSIGNMENT_CONFLICT");
                            }
                            await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                                session,
                                candidate =>
                                    candidate.WorkAssignmentId ==
                                    assignment.Id &&
                                    candidate.IsActive &&
                                    !candidate.IsDeleted,
                                Builders<WorkTemplateAssignee>.Update
                                    .Set(
                                        candidate => candidate.IsActive,
                                        false)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedAtUtc,
                                        now)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedByUserId,
                                        actorUserId),
                                cancellationToken: transactionCt);
                            await _ctx.WorkReportPeriods.UpdateManyAsync(
                                session,
                                candidate =>
                                    candidate.WorkAssignmentId ==
                                    assignment.Id &&
                                    candidate.IsActive &&
                                    !candidate.IsDeleted,
                                Builders<WorkReportPeriod>.Update
                                    .Set(
                                        candidate => candidate.IsActive,
                                        false)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedAtUtc,
                                        now)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedByUserId,
                                        actorUserId),
                                cancellationToken: transactionCt);
                            await _ctx.WorkAssignmentQueueItems
                                .UpdateManyAsync(
                                    session,
                                    candidate =>
                                        candidate.WorkAssignmentId ==
                                        assignment.Id &&
                                        candidate.IsActive &&
                                        !candidate.IsDeleted,
                                    Builders<WorkAssignmentQueueItem>.Update
                                        .Set(
                                            candidate =>
                                                candidate.IsActive,
                                            false)
                                        .Set(
                                            candidate =>
                                                candidate.UpdatedAtUtc,
                                            now)
                                        .Set(
                                            candidate =>
                                                candidate.UpdatedByUserId,
                                            actorUserId),
                                    cancellationToken: transactionCt);
                            var failed = await _ctx.DynamicFlowInstances
                                .UpdateOneAsync(
                                    session,
                                    DynamicFlowRuntimeRevisionContract
                                        .InstanceCas(
                                            instance.Id,
                                            instance.Revision,
                                            DynamicFlowInstanceStates.Active),
                                    Builders<DynamicFlowInstance>.Update
                                        .Set(
                                            candidate => candidate.State,
                                            DynamicFlowInstanceStates.Failed)
                                        .Set(
                                            candidate =>
                                                candidate.LastErrorCode,
                                            DynamicFlowReviewLoopTopologyContract
                                                .CycleExhausted)
                                        .Set(
                                            candidate =>
                                                candidate.UpdatedAtUtc,
                                            now)
                                        .Set(
                                            candidate =>
                                                candidate.UpdatedByUserId,
                                            actorUserId)
                                        .Inc(
                                            candidate =>
                                                candidate.NextEventSequence,
                                            1)
                                        .Inc(
                                            candidate =>
                                                candidate.Revision,
                                            1),
                                    cancellationToken: transactionCt);
                            if (failed.ModifiedCount != 1)
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_REVISION_CONFLICT");
                            }
                            await WorkDirectSourceRevisionFence.IncrementAsync(
                                _ctx,
                                session,
                                instance.WorkId,
                                transactionCt);
                            return;
                        }

                        var nextAttemptNo = currentStep.AttemptNo + 1;
                        var nextReviewCycleNo =
                            currentStep.ReviewCycleNo + 1;
                        var nextStepId =
                            DynamicFlowReviewLoopTopologyContract
                                .BuildStepInstanceId(
                                    instance.Id,
                                    instance.ExecutionEpoch,
                                    topology.ReviewNode.NodeId,
                                    currentStep.BranchId,
                                    nextAttemptNo);
                        var nextAssignmentId =
                            DynamicFlowReviewLoopTopologyContract
                                .BuildAssignmentId(nextStepId);
                        var materializationSequence = eventSequence + 1;
                        var sourceOutbox = await _ctx
                            .DynamicFlowRuntimeOutbox
                            .Find(session, candidate =>
                                candidate.StepInstanceId ==
                                currentStep.Id &&
                                candidate.Status ==
                                DynamicFlowRuntimeOutboxStatuses.Completed)
                            .SingleAsync(transactionCt);
                        var outboxPayload =
                            sourceOutbox.Payload.DeepClone().AsBsonDocument;
                        outboxPayload["commandId"] = sourceEventKey;
                        outboxPayload["actorUserId"] =
                            instance.IssuerUserId;
                        outboxPayload["assignmentId"] = nextAssignmentId;
                        outboxPayload["attemptNo"] = nextAttemptNo;
                        outboxPayload["reviewCycleNo"] =
                            nextReviewCycleNo;
                        outboxPayload["previousAttemptStepInstanceId"] =
                            currentStep.Id;
                        outboxPayload["previousAttemptAssignmentId"] =
                            assignment.Id;
                        outboxPayload["sourceReportId"] =
                            sourceReport.Id;
                        outboxPayload["materializationEventSequence"] =
                            materializationSequence;

                        var nextStep = new DynamicFlowStepInstance
                        {
                            Id = nextStepId,
                            FlowInstanceId = instance.Id,
                            FlowStepId = currentStep.FlowStepId,
                            FlowStepCode = currentStep.FlowStepCode,
                            ExecutionEpoch = currentStep.ExecutionEpoch,
                            DefinitionRevision =
                                currentStep.DefinitionRevision,
                            StepOrder = currentStep.StepOrder,
                            FormNodeId = currentStep.FormNodeId,
                            FormFamilyId = currentStep.FormFamilyId,
                            FormVersionId = currentStep.FormVersionId,
                            FormVersionNo = currentStep.FormVersionNo,
                            FormSchemaHash = currentStep.FormSchemaHash,
                            FormSnapshotHash =
                                currentStep.FormSnapshotHash,
                            TargetUnitId = currentStep.TargetUnitId,
                            ParticipantUserIds =
                                currentStep.ParticipantUserIds.ToList(),
                            ParticipantSnapshotId =
                                currentStep.ParticipantSnapshotId,
                            AttemptNo = nextAttemptNo,
                            ReviewCycleNo = nextReviewCycleNo,
                            PreviousAttemptStepInstanceId =
                                currentStep.Id,
                            PreviousAttemptAssignmentId =
                                assignment.Id,
                            BranchId = currentStep.BranchId,
                            ActivatedByTransitionId = null,
                            NextNodeIds = new List<string>(),
                            IsTerminalNode = true,
                            ResultOwnerIdentity =
                                DynamicFlowReviewLoopTopologyContract
                                    .BuildOwnerIdentity(
                                        instance.Id,
                                        instance.ExecutionEpoch,
                                        currentStep.FlowStepId,
                                        currentStep.BranchId,
                                        nextAttemptNo,
                                        "result"),
                            StatisticOwnerIdentity =
                                DynamicFlowReviewLoopTopologyContract
                                    .BuildOwnerIdentity(
                                        instance.Id,
                                        instance.ExecutionEpoch,
                                        currentStep.FlowStepId,
                                        currentStep.BranchId,
                                        nextAttemptNo,
                                        "statistics"),
                            AssignmentId = nextAssignmentId,
                            State =
                                DynamicFlowStepStates.Materializing,
                            Revision = 1,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            CreatedByUserId = actorUserId,
                            UpdatedByUserId = actorUserId,
                            IsDeleted = false
                        };
                        var result = new BsonDocument
                        {
                            { "flowInstanceId", instance.Id },
                            {
                                "status",
                                "ACCEPTED_PENDING_MATERIALIZATION"
                            },
                            { "sourceReportId", sourceReport.Id },
                            {
                                "previousStepInstanceId",
                                currentStep.Id
                            },
                            {
                                "previousAssignmentId",
                                assignment.Id
                            },
                            { "nextStepInstanceId", nextStepId },
                            { "nextAssignmentId", nextAssignmentId },
                            { "attemptNo", nextAttemptNo },
                            { "reviewCycleNo", nextReviewCycleNo },
                            {
                                "maxReviewCycles",
                                topology.MaxReviewCycles
                            },
                            { "eventId", eventId },
                            {
                                "materializationEventSequence",
                                materializationSequence
                            }
                        };
                        var eventPayload = new BsonDocument
                        {
                            { "sourceReportId", sourceReport.Id },
                            {
                                "previousStepInstanceId",
                                currentStep.Id
                            },
                            {
                                "previousAssignmentId",
                                assignment.Id
                            },
                            { "nextStepInstanceId", nextStepId },
                            { "nextAssignmentId", nextAssignmentId },
                            { "reviewCycleNo", nextReviewCycleNo },
                            {
                                "maxReviewCycles",
                                topology.MaxReviewCycles
                            },
                            {
                                "definitionRevision",
                                instance.DefinitionRevision
                            },
                            {
                                "topologyHash",
                                instance.TopologySnapshotHash
                            }
                        };

                        await _ctx.DynamicFlowRuntimeCommandReceipts
                            .InsertOneAsync(
                                session,
                                BuildReviewAdvanceReceipt(
                                    receiptId,
                                    instance,
                                    sourceEventKey,
                                    requestHash,
                                    actorUserId,
                                    result,
                                    now),
                                cancellationToken: transactionCt);
                        await _ctx.DynamicFlowStepInstances.InsertOneAsync(
                            session,
                            nextStep,
                            cancellationToken: transactionCt);
                        await _ctx.DynamicFlowRuntimeOutbox.InsertOneAsync(
                            session,
                            new DynamicFlowRuntimeOutboxItem
                            {
                                Id = StableObjectId(
                                    $"{nextStepId}\noutbox\nmaterialize"),
                                FlowInstanceId = instance.Id,
                                StepInstanceId = nextStepId,
                                Operation =
                                    DynamicFlowRuntimeMaterializationOperations
                                        .MaterializeReviewAttemptAssignment,
                                DedupeKey =
                                    $"{instance.Id}:{nextStep.FlowStepId}:{nextStep.TargetUnitId}:{nextAttemptNo}:materialize",
                                Payload = outboxPayload,
                                PayloadHash =
                                    Hash(outboxPayload.ToJson()),
                                Status =
                                    DynamicFlowRuntimeOutboxStatuses.Pending,
                                AttemptCount = 0,
                                RepairEpoch = 0,
                                NextAttemptAtUtc = now,
                                CreatedAtUtc = now,
                                UpdatedAtUtc = now,
                                UpdatedByUserId = actorUserId
                            },
                            cancellationToken: transactionCt);
                        await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                            session,
                            new DynamicFlowRuntimeEvent
                            {
                                Id = eventId,
                                FlowInstanceId = instance.Id,
                                StepInstanceId = nextStepId,
                                ExecutionEpoch = instance.ExecutionEpoch,
                                BranchId = nextStep.BranchId,
                                AttemptNo = nextAttemptNo,
                                Sequence = eventSequence,
                                EventType =
                                    DynamicFlowReviewLoopTopologyContract
                                        .ReviewAttemptCreatedEvent,
                                CommandId = sourceEventKey,
                                CorrelationId = sourceEventKey,
                                SourceEventKey = sourceEventKey,
                                FromState =
                                    DynamicFlowStepStates.Returned,
                                ToState =
                                    DynamicFlowStepStates.Materializing,
                                FromRevision = currentStep.Revision,
                                ToRevision = 1,
                                ReasonCode = "REVIEW_RETURNED",
                                AffectedRefs =
                                [
                                    $"step:{currentStep.Id}",
                                    $"step:{nextStepId}",
                                    $"assignment:{assignment.Id}",
                                    $"assignment:{nextAssignmentId}",
                                    $"report:{sourceReport.Id}"
                                ],
                                ActorUserId = actorUserId,
                                VisibleUnitIds = new[]
                                    {
                                        currentStep.TargetUnitId,
                                        instance.IssuerUnitId
                                    }
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(
                                        value => value,
                                        StringComparer.Ordinal)
                                    .ToList(),
                                Payload = eventPayload,
                                PayloadHash =
                                    Hash(eventPayload.ToJson()),
                                OccurredAtUtc = now
                            },
                            cancellationToken: transactionCt);

                        var superseded = await _ctx
                            .DynamicFlowStepInstances
                            .UpdateOneAsync(
                                session,
                                DynamicFlowRuntimeRevisionContract.StepCas(
                                    currentStep.Id,
                                    currentStep.Revision,
                                    DynamicFlowStepStates.Returned),
                                Builders<DynamicFlowStepInstance>.Update
                                    .Set(
                                        candidate =>
                                            candidate
                                                .SupersededByStepInstanceId,
                                        nextStepId)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedAtUtc,
                                        now)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedByUserId,
                                        actorUserId)
                                    .Inc(
                                        candidate => candidate.Revision,
                                        1),
                                cancellationToken: transactionCt);
                        if (superseded.ModifiedCount != 1)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_REVISION_CONFLICT");
                        }
                        var invalidated = await _ctx.WorkAssignments
                            .UpdateOneAsync(
                                session,
                                candidate =>
                                    candidate.Id == assignment.Id &&
                                    candidate.FlowInstanceId ==
                                    instance.Id &&
                                    candidate.FlowEffectiveStatus ==
                                    DynamicFlowEffectiveStatuses.Effective &&
                                    !candidate.IsDeleted,
                                Builders<WorkAssignment>.Update
                                    .Set(
                                        candidate =>
                                            candidate.FlowEffectiveStatus,
                                        DynamicFlowEffectiveStatuses
                                            .Invalidated)
                                    .Set(
                                        candidate =>
                                            candidate
                                                .InvalidatedByFlowEventId,
                                        eventId)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedAtUtc,
                                        now)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedByUserId,
                                        actorUserId),
                                cancellationToken: transactionCt);
                        if (invalidated.ModifiedCount != 1)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_REVIEW_RETURN_ASSIGNMENT_CONFLICT");
                        }
                        await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                            session,
                            candidate =>
                                candidate.WorkAssignmentId ==
                                assignment.Id &&
                                candidate.IsActive &&
                                !candidate.IsDeleted,
                            Builders<WorkTemplateAssignee>.Update
                                .Set(
                                    candidate => candidate.IsActive,
                                    false)
                                .Set(
                                    candidate =>
                                        candidate.UpdatedAtUtc,
                                    now)
                                .Set(
                                    candidate =>
                                        candidate.UpdatedByUserId,
                                    actorUserId),
                            cancellationToken: transactionCt);
                        await _ctx.WorkReportPeriods.UpdateManyAsync(
                            session,
                            candidate =>
                                candidate.WorkAssignmentId ==
                                assignment.Id &&
                                candidate.IsActive &&
                                !candidate.IsDeleted,
                            Builders<WorkReportPeriod>.Update
                                .Set(
                                    candidate => candidate.IsActive,
                                    false)
                                .Set(
                                    candidate =>
                                        candidate.UpdatedAtUtc,
                                    now)
                                .Set(
                                    candidate =>
                                        candidate.UpdatedByUserId,
                                    actorUserId),
                            cancellationToken: transactionCt);
                        await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
                            session,
                            candidate =>
                                candidate.WorkAssignmentId ==
                                assignment.Id &&
                                candidate.IsActive &&
                                !candidate.IsDeleted,
                            Builders<WorkAssignmentQueueItem>.Update
                                .Set(
                                    candidate => candidate.IsActive,
                                    false)
                                .Set(
                                    candidate =>
                                        candidate.UpdatedAtUtc,
                                    now)
                                .Set(
                                    candidate =>
                                        candidate.UpdatedByUserId,
                                    actorUserId),
                            cancellationToken: transactionCt);
                        var advanced = await _ctx.DynamicFlowInstances
                            .UpdateOneAsync(
                                session,
                                DynamicFlowRuntimeRevisionContract.InstanceCas(
                                    instance.Id,
                                    instance.Revision,
                                    DynamicFlowInstanceStates.Active),
                                Builders<DynamicFlowInstance>.Update
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedAtUtc,
                                        now)
                                    .Set(
                                        candidate =>
                                            candidate.UpdatedByUserId,
                                        actorUserId)
                                    .Inc(
                                        candidate =>
                                            candidate.NextEventSequence,
                                        2)
                                    .Inc(
                                        candidate => candidate.Revision,
                                        1),
                                cancellationToken: transactionCt);
                        if (advanced.ModifiedCount != 1)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_REVISION_CONFLICT");
                        }
                        await WorkDirectSourceRevisionFence.IncrementAsync(
                            _ctx,
                            session,
                            instance.WorkId,
                            transactionCt);
                    },
                    ct);
                return;
            }
            catch (Exception error) when (
                attempt < MaxRaceAttempts &&
                IsRetryableRace(error))
            {
                await Task.Yield();
            }
        }

        throw new InvalidOperationException(
            "DYNAMIC_FLOW_REVIEW_RETURN_RACE_EXHAUSTED");
    }

    private static DynamicFlowRuntimeCommandReceipt BuildReviewAdvanceReceipt(
        string receiptId,
        DynamicFlowInstance instance,
        string sourceEventKey,
        string requestHash,
        string actorUserId,
        BsonDocument result,
        DateTime now)
        => new()
        {
            Id = receiptId,
            ScopeKind = DynamicFlowCommandScopeKinds.Instance,
            ScopeId = instance.Id,
            WorkId = instance.WorkId,
            FlowTemplateVersionId = instance.FlowTemplateVersionId,
            FlowInstanceId = instance.Id,
            CommandType =
                DynamicFlowReviewLoopTopologyContract.ReviewAdvanceCommand,
            CommandId = sourceEventKey,
            RequestHash = requestHash,
            CommandIdentityHash = Hash(
                $"{actorUserId}\n{instance.Id}\n{sourceEventKey}\n{DynamicFlowReviewLoopTopologyContract.ReviewAdvanceCommand}"),
            SnapshotToken = Hash(
                $"{requestHash}\n{instance.TopologySnapshotHash}"),
            ExpectedRevision = instance.Revision,
            Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
            ResultSnapshot = result,
            ResultSnapshotHash = Hash(result.ToJson()),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            UpdatedByUserId = actorUserId,
            CompletedAtUtc = now
        };

    private async Task<DynamicFlowRuntimeStateProjectionResult> ProjectCoreWithRetryAsync(
        WorkAssignment assignment,
        string commandType,
        string sourceCommandId,
        string sourceEventKey,
        string requestHash,
        string projectionFingerprint,
        string actorUserId,
        string? sourceReportId,
        string? reportLifecycleEntryKey,
        int? sourceLifecycleRevision,
        string? sourceLifecycleStatus,
        bool? sourceLifecycleIsActive,
        string targetState,
        string reasonCode,
        bool allowInstanceCompletion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_INSTANCE_MISSING");
        var flowInstanceId = assignment.FlowInstanceId;
        var receiptId = StableObjectId(
            $"{DynamicFlowCommandScopeKinds.Instance}\n{flowInstanceId}\n{commandType}\n{sourceEventKey}");

        for (var attempt = 1; attempt <= MaxRaceAttempts; attempt++)
        {
            var replay = await TryReadProjectionReplayAsync(
                receiptId,
                commandType,
                sourceEventKey,
                requestHash,
                ct);
            if (replay is not null)
                return replay with { Replayed = true };

            try
            {
                _faults.ThrowIfConfigured(
                    sourceCommandId,
                    DynamicFlowRuntimeStateProjectionFaultPoints.BeforeTransaction);
                var committed = await _transactions.ExecuteAsync(
                    async (session, transactionCt) =>
                    {
                        var existing = await _ctx.DynamicFlowRuntimeCommandReceipts
                            .Find(session, x => x.Id == receiptId)
                            .FirstOrDefaultAsync(transactionCt);
                        if (existing is not null)
                        {
                            return ParseProjectionReplay(
                                existing,
                                commandType,
                                sourceEventKey,
                                requestHash);
                        }

                        var step = await _ctx.DynamicFlowStepInstances
                            .Find(
                                session,
                                x =>
                                    x.FlowInstanceId == flowInstanceId &&
                                    x.AssignmentId == assignment.Id &&
                                    !x.IsDeleted)
                            .SingleAsync(transactionCt);
                        var instance = await _ctx.DynamicFlowInstances
                            .Find(
                                session,
                                x => x.Id == flowInstanceId && !x.IsDeleted)
                            .SingleAsync(transactionCt);
                        if (instance.State is DynamicFlowInstanceStates.Failed
                            or DynamicFlowInstanceStates.Terminated)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_INSTANCE_TERMINAL");
                        }
                        if (step.State is DynamicFlowStepStates.Partial
                            or DynamicFlowStepStates.Retrying
                            or DynamicFlowStepStates.Reconciled)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_RECOVERY_REQUIRED");
                        }

                        List<DynamicFlowStepInstance>? siblingSteps = null;
                        var internalCompletionEligible = false;
                        string? completionArchetypeId = null;
                        DynamicFlowGatewayInstance? joinGateway = null;
                        List<string>? joinArrivedAfter = null;
                        var joinContributionEligible = false;
                        var willSatisfyJoin = false;
                        var isQuorumJoin = false;
                        var isLateJoinContribution = false;
                        var requiredContributionCount = 0;
                        List<string> cancelledContributionIds = new();
                        List<DynamicFlowStepInstance> cancelledSiblingSteps = new();
                        if (allowInstanceCompletion ||
                            commandType ==
                            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete)
                        {
                            siblingSteps = await _ctx.DynamicFlowStepInstances
                                .Find(
                                    session,
                                    x => x.FlowInstanceId == instance.Id && !x.IsDeleted)
                                .ToListAsync(transactionCt);
                            completionArchetypeId = await ResolveStepArchetypeAsync(
                                session,
                                instance.Id,
                                step.Id,
                                transactionCt);
                            if (completionArchetypeId ==
                                DynamicFlowFinalizeTopologyContract
                                    .ArchetypeId)
                            {
                                siblingSteps = siblingSteps
                                    .Where(candidate =>
                                        candidate.ExecutionEpoch ==
                                            instance.ExecutionEpoch &&
                                        candidate.IsCanonicalEpoch != false)
                                    .ToList();
                            }
                            internalCompletionEligible =
                                completionArchetypeId == "FLOW-T01" &&
                                siblingSteps.Count == 1 ||
                                completionArchetypeId == "FLOW-T03" &&
                                step.IsTerminalNode ||
                                completionArchetypeId == "FLOW-T04" &&
                                step.IsTerminalNode &&
                                step.GatewayInstanceId is not null &&
                                step.GatewayVersion == 1 &&
                                step.ContributionId is not null ||
                                completionArchetypeId ==
                                    DynamicFlowTypedConditionalTopologyContract
                                        .ArchetypeId &&
                                step.IsTerminalNode &&
                                step.GatewayInstanceId is not null &&
                                step.GatewayVersion ==
                                    DynamicFlowTypedConditionalTopologyContract
                                        .GatewayVersion &&
                                step.ContributionId is not null ||
                                completionArchetypeId ==
                                    DynamicFlowJoinAllTopologyContract.ArchetypeId &&
                                step.IsTerminalNode &&
                                step.GatewayInstanceId is not null &&
                                step.GatewayVersion ==
                                    DynamicFlowJoinAllTopologyContract
                                        .GatewayVersion &&
                                    step.ContributionId is not null;
                            internalCompletionEligible =
                                internalCompletionEligible ||
                                completionArchetypeId ==
                                    DynamicFlowSupplementalTopologyContract
                                        .ArchetypeId &&
                                (step.IsSupplemental ||
                                 step.IsTerminalNode) ||
                                completionArchetypeId ==
                                    DynamicFlowFinalizeTopologyContract
                                        .ArchetypeId &&
                                step.ExecutionEpoch ==
                                    instance.ExecutionEpoch &&
                                step.IsCanonicalEpoch != false ||
                                completionArchetypeId ==
                                    DynamicFlowReviewLoopTopologyContract
                                        .ArchetypeId &&
                                step.IsTerminalNode &&
                                step.SupersededByStepInstanceId is null ||
                                completionArchetypeId ==
                                    DynamicFlowJoinQuorumTopologyContract
                                        .ArchetypeId &&
                                step.IsTerminalNode &&
                                step.GatewayInstanceId is not null &&
                                step.GatewayVersion ==
                                    DynamicFlowJoinQuorumTopologyContract
                                        .GatewayVersion &&
                                step.ContributionId is not null;
                            joinContributionEligible =
                                commandType ==
                                    DynamicFlowRuntimeStateProjectionOperations
                                        .AssignmentComplete &&
                                completionArchetypeId is
                                    DynamicFlowJoinAllTopologyContract.ArchetypeId or
                                    DynamicFlowJoinQuorumTopologyContract.ArchetypeId &&
                                internalCompletionEligible;
                            if (joinContributionEligible)
                            {
                                joinGateway = await _ctx.DynamicFlowGatewayInstances
                                    .Find(
                                        session,
                                        gateway =>
                                            gateway.FlowInstanceId == instance.Id &&
                                            gateway.GatewayInstanceId ==
                                                step.GatewayInstanceId &&
                                            gateway.GatewayVersion ==
                                                step.GatewayVersion)
                                    .SingleAsync(transactionCt);
                                var arrived = await _ctx
                                    .DynamicFlowGatewayContributions
                                    .Find(
                                        session,
                                        contribution =>
                                            contribution.FlowInstanceId ==
                                                instance.Id &&
                                            contribution.GatewayInstanceId ==
                                                joinGateway.GatewayInstanceId &&
                                            contribution.GatewayVersion ==
                                                joinGateway.GatewayVersion)
                                    .ToListAsync(transactionCt);
                                var arrivedIds = arrived
                                    .Where(item =>
                                        item.EffectiveStatus ==
                                        DynamicFlowGatewayContributionOutcomes
                                            .Effective)
                                    .Select(item => item.ContributionId)
                                    .OrderBy(value => value, StringComparer.Ordinal)
                                    .ToArray();
                                var expectedIds = joinGateway
                                    .ExpectedContributionIds
                                    .OrderBy(value => value, StringComparer.Ordinal)
                                    .ToArray();
                                isQuorumJoin = completionArchetypeId ==
                                    DynamicFlowJoinQuorumTopologyContract.ArchetypeId;
                                requiredContributionCount = isQuorumJoin
                                    ? joinGateway.RequiredContributionCount
                                    : expectedIds.Length;
                                isLateJoinContribution =
                                    isQuorumJoin &&
                                    joinGateway.State ==
                                        DynamicFlowGatewayStates.Satisfied &&
                                    joinGateway.CancelledContributionIds.Contains(
                                        step.ContributionId!,
                                        StringComparer.Ordinal);
                                var gatewayKindValid = isQuorumJoin
                                    ? joinGateway.GatewayKind is
                                        DynamicFlowGatewayKinds.JoinAny or
                                        DynamicFlowGatewayKinds.JoinNOfM
                                    : joinGateway.GatewayKind ==
                                      DynamicFlowGatewayKinds.JoinAll;
                                if (!gatewayKindValid ||
                                    expectedIds.Length != 2 ||
                                    requiredContributionCount < 1 ||
                                    requiredContributionCount > expectedIds.Length ||
                                    expectedIds.Distinct(StringComparer.Ordinal)
                                        .Count() != expectedIds.Length ||
                                    arrivedIds.Distinct(StringComparer.Ordinal)
                                        .Count() != arrivedIds.Length ||
                                    arrivedIds.Any(value =>
                                        !expectedIds.Contains(
                                            value,
                                            StringComparer.Ordinal)) ||
                                    !expectedIds.Contains(
                                        step.ContributionId!,
                                        StringComparer.Ordinal) ||
                                    arrived.Any(item =>
                                        item.ContributionId ==
                                        step.ContributionId) ||
                                    (!isLateJoinContribution &&
                                     joinGateway.State !=
                                        DynamicFlowGatewayStates.Collecting))
                                {
                                    throw new InvalidOperationException(
                                        "DYNAMIC_FLOW_JOIN_LEDGER_DRIFT");
                                }
                                if (!assignment.IsActive ||
                                    (!isLateJoinContribution &&
                                     assignment.FlowEffectiveStatus !=
                                        DynamicFlowEffectiveStatuses.Effective) ||
                                    (isLateJoinContribution &&
                                     assignment.FlowEffectiveStatus !=
                                        DynamicFlowEffectiveStatuses.Invalidated))
                                {
                                    throw new InvalidOperationException(
                                        "DYNAMIC_FLOW_JOIN_CONTRIBUTION_NOT_EFFECTIVE");
                                }

                                joinArrivedAfter = isLateJoinContribution
                                    ? arrivedIds.ToList()
                                    : arrivedIds
                                        .Append(step.ContributionId!)
                                        .OrderBy(value => value, StringComparer.Ordinal)
                                        .ToList();
                                willSatisfyJoin =
                                    !isLateJoinContribution &&
                                    joinArrivedAfter.Count >=
                                        requiredContributionCount;
                                if (willSatisfyJoin && isQuorumJoin)
                                {
                                    cancelledContributionIds = expectedIds
                                        .Except(
                                            joinArrivedAfter,
                                            StringComparer.Ordinal)
                                        .OrderBy(value => value, StringComparer.Ordinal)
                                        .ToList();
                                    cancelledSiblingSteps = siblingSteps!
                                        .Where(item =>
                                            item.GatewayInstanceId ==
                                                joinGateway.GatewayInstanceId &&
                                            item.GatewayVersion ==
                                                joinGateway.GatewayVersion &&
                                            item.ContributionId is not null &&
                                            cancelledContributionIds.Contains(
                                                item.ContributionId,
                                                StringComparer.Ordinal))
                                        .ToList();
                                }
                            }
                        }

                        var effectiveTarget = targetState;
                        var effectiveFingerprint = projectionFingerprint;
                        string? effectiveReportId = sourceReportId;
                        var effectiveLifecycleEntryKey = reportLifecycleEntryKey;
                        var effectiveLifecycleCommandId = sourceCommandId;
                        var effectiveLifecycleRevision = sourceLifecycleRevision;
                        var effectiveLifecycleStatus = sourceLifecycleStatus;
                        var effectiveLifecycleIsActive = sourceLifecycleIsActive;
                        if (commandType is DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle
                            or DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
                        {
                            var current = commandType ==
                                          DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle
                                ? await ResolveReportProjectionAsync(
                                    session,
                                    assignment,
                                    transactionCt,
                                    sourceReportId,
                                    sourceLifecycleRevision,
                                    sourceLifecycleStatus,
                                    sourceLifecycleIsActive,
                                    reportLifecycleEntryKey,
                                    sourceCommandId,
                                    reasonCode)
                                : await ResolveReportProjectionAsync(
                                    session,
                                    assignment,
                                    transactionCt);
                            effectiveTarget = current.TargetState;
                            effectiveFingerprint = current.Fingerprint;
                            effectiveReportId = current.CanonicalReportId;
                            effectiveLifecycleEntryKey = current.LifecycleEntryKey;
                            effectiveLifecycleCommandId = current.LifecycleCommandId;
                            effectiveLifecycleRevision = current.LifecycleRevision;
                            effectiveLifecycleStatus = current.LifecycleStatus;
                            effectiveLifecycleIsActive = current.LifecycleIsActive;
                        }
                        if (step.State == DynamicFlowStepStates.Completed &&
                            commandType is
                                DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle or
                                DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
                        {
                            // A delayed P3 outbox entry remains auditable but cannot reopen a
                            // terminal step after the assignment completion commit. Reconcile
                            // may still repair report provenance through a typed no-op event.
                            effectiveTarget = DynamicFlowStepStates.Completed;
                        }
                        if (isLateJoinContribution)
                        {
                            effectiveTarget =
                                DynamicFlowStepStates.CancelledByGateway;
                        }
                        if (commandType ==
                            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete)
                        {
                            if (completionArchetypeId is not (
                                    "FLOW-T01" or
                                    "FLOW-T02" or
                                    "FLOW-T03" or
                                    "FLOW-T04" or
                                    "FLOW-T05" or
                                    "FLOW-T06" or
                                    "FLOW-T07" or
                                    "FLOW-T08" or
                                    "FLOW-T09" or
                                    "FLOW-T11" or
                                    "FLOW-T12"))
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_OWNERSHIP_DRIFT");
                            }
                            if (completionArchetypeId == "FLOW-T01" &&
                                !internalCompletionEligible)
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_RUNTIME_T01_COMPLETION_REQUIRES_SOLE_STEP");
                            }
                            if (completionArchetypeId is
                                "FLOW-T02" or
                                DynamicFlowSubflowTopologyContract.ArchetypeId)
                            {
                                // FLOW-T02 aggregate finalization and FLOW-T09
                                // child completion are owned by their topology
                                // projectors, not by assignment completion.
                                effectiveTarget = step.State;
                            }
                            else if (step.State is not
                                     (DynamicFlowStepStates.Approved or
                                      DynamicFlowStepStates.Completed) &&
                                     !(isLateJoinContribution &&
                                       step.State ==
                                       DynamicFlowStepStates
                                           .CancelledByGateway))
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_RUNTIME_ASSIGNMENT_COMPLETION_REQUIRES_APPROVED_STEP");
                            }
                        }
                        if (instance.State != DynamicFlowInstanceStates.Active &&
                            !(instance.State == DynamicFlowInstanceStates.Completed &&
                              (step.State == DynamicFlowStepStates.Completed &&
                               effectiveTarget == DynamicFlowStepStates.Completed ||
                               isLateJoinContribution &&
                               step.State ==
                                   DynamicFlowStepStates.CancelledByGateway &&
                               effectiveTarget ==
                                   DynamicFlowStepStates.CancelledByGateway)))
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_RECOVERY_REQUIRED");
                        }

                        var path = DynamicFlowRuntimeStateProjectionPolicy.BuildStepPath(
                            step.State,
                            effectiveTarget);
                        var transitions = path.Count == 0
                            ? new[] { (From: step.State, To: step.State) }
                            : BuildTransitions(step.State, path);
                        var stepFromRevision = step.Revision;
                        var stepToRevision = step.Revision + transitions.Length;
                        var instanceFromRevision = instance.Revision;
                        var instanceToRevision = instance.Revision + 1;
                        var eventIds = Enumerable.Range(0, transitions.Length)
                            .Select(index => StableObjectId(
                                $"{flowInstanceId}\nstate-event\n{receiptId}\n{index}"))
                            .ToArray();
                        var eventSequenceStart = instance.NextEventSequence;
                        var willCompleteInstance = false;
                        if (allowInstanceCompletion &&
                            internalCompletionEligible &&
                            effectiveTarget == DynamicFlowStepStates.Completed &&
                            instance.State == DynamicFlowInstanceStates.Active)
                        {
                            if (joinContributionEligible)
                            {
                                willCompleteInstance = willSatisfyJoin;
                            }
                            else
                            {
                                siblingSteps ??= await _ctx.DynamicFlowStepInstances
                                    .Find(
                                        session,
                                        x =>
                                            x.FlowInstanceId == instance.Id &&
                                            !x.IsDeleted)
                                    .ToListAsync(transactionCt);
                                willCompleteInstance =
                                    completionArchetypeId ==
                                    DynamicFlowReviewLoopTopologyContract
                                        .ArchetypeId
                                        ? siblingSteps.All(item =>
                                            item.Id == step.Id ||
                                            item.SupersededByStepInstanceId
                                                is not null &&
                                            item.State ==
                                            DynamicFlowStepStates.Returned)
                                        : completionArchetypeId ==
                                          DynamicFlowSupplementalTopologyContract
                                              .ArchetypeId
                                            ? DynamicFlowSupplementalTopologyContract
                                                .CompletionSatisfied(
                                                    siblingSteps,
                                                    step.Id)
                                        : siblingSteps.All(item =>
                                            item.Id == step.Id ||
                                            item.State ==
                                            DynamicFlowStepStates.Completed);
                            }
                        }
                        var joinContributionEventId =
                            joinContributionEligible &&
                            !isLateJoinContribution
                            ? DynamicFlowJoinAllTopologyContract
                                .BuildContributionEventId(
                                    joinGateway!.GatewayInstanceId,
                                    joinGateway.GatewayVersion,
                                    step.ContributionId!)
                            : null;
                        var joinLateEventId = isLateJoinContribution
                            ? DynamicFlowJoinQuorumTopologyContract
                                .BuildLateEventId(
                                    joinGateway!.GatewayInstanceId,
                                    joinGateway.GatewayVersion,
                                    step.ContributionId!)
                            : null;
                        var joinSatisfiedEventId = willSatisfyJoin
                            ? DynamicFlowJoinAllTopologyContract
                                .BuildReleaseEventId(
                                    joinGateway!.GatewayInstanceId,
                                    joinGateway.GatewayVersion)
                            : null;
                        var joinCancelledEventIds = cancelledSiblingSteps
                            .Select(cancelled => StableObjectId(
                                $"{joinGateway!.GatewayInstanceId}\n{joinGateway.GatewayVersion}\n{cancelled.ContributionId}\ncancelled-by-gateway"))
                            .ToArray();
                        var instanceEventId = willCompleteInstance
                            ? StableObjectId(
                                $"{flowInstanceId}\nstate-event\n{receiptId}\ninstance-completed")
                            : null;
                        var eventCount =
                            transitions.Length +
                            (joinContributionEventId is not null ? 1 : 0) +
                            (joinLateEventId is not null ? 1 : 0) +
                            (willSatisfyJoin ? 1 : 0) +
                            joinCancelledEventIds.Length +
                            (willCompleteInstance ? 1 : 0);
                        if (willCompleteInstance)
                            instanceToRevision = instance.Revision + 1;

                        var resultSnapshot = new BsonDocument
                        {
                            { "flowInstanceId", instance.Id },
                            { "stepInstanceId", step.Id },
                            { "assignmentId", assignment.Id },
                            { "sourceEventKey", sourceEventKey },
                            { "sourceCommandId", sourceCommandId },
                            { "commandType", commandType },
                            { "fromState", step.State },
                            { "toState", effectiveTarget },
                            { "fromStepRevision", stepFromRevision },
                            { "toStepRevision", stepToRevision },
                            { "fromInstanceRevision", instanceFromRevision },
                            { "toInstanceRevision", instanceToRevision },
                            { "projectionFingerprint", effectiveFingerprint },
                            { "instanceCompleted", willCompleteInstance },
                            {
                                "eventIds",
                                new BsonArray(eventIds
                                    .Append(joinContributionEventId)
                                    .Append(joinLateEventId)
                                    .Append(joinSatisfiedEventId)
                                    .Concat(joinCancelledEventIds)
                                    .Append(instanceEventId)
                                    .Where(value => value is not null))
                            }
                        };
                        if (commandType ==
                            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete)
                        {
                            resultSnapshot["assignmentCompletedAtUtc"] =
                                assignment.CompletedAtUtc!.Value
                                    .ToUniversalTime()
                                    .ToString("O");
                            resultSnapshot["assignmentCompletedDate"] =
                                assignment.CompletedDate?.ToUniversalTime().ToString("O") ??
                                string.Empty;
                            resultSnapshot["assignmentCompletedByUserId"] =
                                assignment.CompletedByUserId ?? actorUserId;
                        }
                        var now = DateTime.UtcNow;
                        var receipt = new DynamicFlowRuntimeCommandReceipt
                        {
                            Id = receiptId,
                            ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                            ScopeId = instance.Id,
                            WorkId = instance.WorkId,
                            FlowTemplateVersionId = instance.FlowTemplateVersionId,
                            FlowInstanceId = instance.Id,
                            CommandType = commandType,
                            CommandId = sourceEventKey,
                            RequestHash = requestHash,
                            CommandIdentityHash = Hash(
                                $"{actorUserId}\n{assignment.Id}\n{sourceCommandId}\n{sourceEventKey}"),
                            SnapshotToken = Hash(
                                $"{requestHash}\n{effectiveFingerprint}\n{instance.FlowPayloadHash}"),
                            ExpectedRevision = step.Revision,
                            Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
                            ResultSnapshot = resultSnapshot,
                            ResultSnapshotHash = Hash(resultSnapshot.ToJson()),
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            UpdatedByUserId = actorUserId,
                            CompletedAtUtc = now
                        };

                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeReceiptWrite);
                        await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                            session,
                            receipt,
                            cancellationToken: transactionCt);
                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.AfterReceiptWrite);

                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeStepWrite);
                        var stepUpdateDefinition = Builders<DynamicFlowStepInstance>.Update
                            .Set(x => x.State, effectiveTarget)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId)
                            .Inc(x => x.Revision, transitions.Length);
                        if (commandType == DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle)
                        {
                            stepUpdateDefinition = stepUpdateDefinition
                                .Set(x => x.ReportId, effectiveReportId)
                                .Set(x => x.ReportLifecycleRevision, effectiveLifecycleRevision)
                                .Set(x => x.ReportLifecycleEntryKey, effectiveLifecycleEntryKey)
                                .Set(x => x.ReportLifecycleCommandId, effectiveLifecycleCommandId)
                                .Set(x => x.ReportLifecycleStatus, effectiveLifecycleStatus)
                                .Set(x => x.ReportLifecycleIsActive, effectiveLifecycleIsActive);
                        }
                        else if (commandType == DynamicFlowRuntimeStateProjectionOperations.StateReconcile)
                        {
                            stepUpdateDefinition = stepUpdateDefinition
                                .Set(x => x.ReportId, effectiveReportId)
                                .Set(x => x.ReportLifecycleRevision, effectiveLifecycleRevision)
                                .Set(x => x.ReportLifecycleEntryKey, effectiveLifecycleEntryKey)
                                .Set(x => x.ReportLifecycleCommandId, effectiveLifecycleCommandId)
                                .Set(x => x.ReportLifecycleStatus, effectiveLifecycleStatus)
                                .Set(x => x.ReportLifecycleIsActive, effectiveLifecycleIsActive);
                        }
                        var stepUpdate = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.StepCas(
                                step.Id,
                                step.Revision,
                                step.State),
                            stepUpdateDefinition,
                            cancellationToken: transactionCt);
                        if (stepUpdate.ModifiedCount != 1)
                            throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.AfterStepWrite);

                        if (joinContributionEligible)
                        {
                            await _ctx.DynamicFlowGatewayContributions.InsertOneAsync(
                                session,
                                new DynamicFlowGatewayContribution
                                {
                                    Id = DynamicFlowJoinAllTopologyContract
                                        .BuildContributionLedgerId(
                                            joinGateway!.GatewayInstanceId,
                                            joinGateway.GatewayVersion,
                                            step.ContributionId!),
                                    FlowInstanceId = instance.Id,
                                    GatewayInstanceId =
                                        joinGateway.GatewayInstanceId,
                                    GatewayVersion = joinGateway.GatewayVersion,
                                    ContributionId = step.ContributionId!,
                                    BranchId = step.BranchId,
                                    StepInstanceId = step.Id,
                                    AssignmentId = assignment.Id,
                                    EffectiveStatus =
                                        isLateJoinContribution
                                            ? DynamicFlowGatewayContributionOutcomes
                                                .LateIgnored
                                            : DynamicFlowGatewayContributionOutcomes
                                                .Effective,
                                    SourceCommandId = sourceCommandId,
                                    ArrivedAtUtc = now,
                                    ArrivedByUserId = actorUserId
                                },
                                cancellationToken: transactionCt);
                            _faults.ThrowIfConfigured(
                                sourceCommandId,
                                DynamicFlowRuntimeStateProjectionFaultPoints
                                    .AfterJoinContributionWrite);

                            var gatewayUpdate =
                                Builders<DynamicFlowGatewayInstance>.Update
                                    .Set(
                                        gateway => gateway.ArrivedContributionIds,
                                        joinArrivedAfter!)
                                    .Set(
                                        gateway => gateway.UpdatedAtUtc,
                                        now)
                                    .Set(
                                        gateway => gateway.UpdatedByUserId,
                                        actorUserId)
                                    .Inc(gateway => gateway.Revision, 1);
                            if (isLateJoinContribution)
                            {
                                gatewayUpdate = gatewayUpdate.Set(
                                    gateway => gateway.LateContributionIds,
                                    joinGateway.LateContributionIds
                                        .Append(step.ContributionId!)
                                        .Distinct(StringComparer.Ordinal)
                                        .OrderBy(
                                            value => value,
                                            StringComparer.Ordinal)
                                        .ToList());
                            }
                            if (willSatisfyJoin)
                            {
                                gatewayUpdate = gatewayUpdate
                                    .Set(
                                        gateway => gateway.State,
                                        DynamicFlowGatewayStates.Satisfied)
                                    .Set(
                                        gateway => gateway.ReleasedAtUtc,
                                        now)
                                    .Set(
                                        gateway => gateway.WinnerContributionId,
                                        step.ContributionId)
                                    .Set(
                                        gateway => gateway
                                            .CancelledContributionIds,
                                        cancelledContributionIds);
                            }
                            var gatewayResult =
                                await _ctx.DynamicFlowGatewayInstances.UpdateOneAsync(
                                    session,
                                    gateway =>
                                        gateway.Id == joinGateway.Id &&
                                        gateway.Revision == joinGateway.Revision &&
                                        gateway.State ==
                                            (isLateJoinContribution
                                                ? DynamicFlowGatewayStates.Satisfied
                                                : DynamicFlowGatewayStates.Collecting),
                                    gatewayUpdate,
                                    cancellationToken: transactionCt);
                            if (gatewayResult.ModifiedCount != 1)
                            {
                                throw new InvalidOperationException(
                                    "DYNAMIC_FLOW_REVISION_CONFLICT");
                            }
                            if (cancelledSiblingSteps.Count > 0)
                            {
                                var cancelledStepIds = cancelledSiblingSteps
                                    .Select(item => item.Id)
                                    .ToArray();
                                var cancelledAssignments = cancelledSiblingSteps
                                    .Select(item => item.AssignmentId!)
                                    .ToArray();
                                var cancelledStepsResult =
                                    await _ctx.DynamicFlowStepInstances.UpdateManyAsync(
                                        session,
                                        candidate =>
                                            cancelledStepIds.Contains(candidate.Id) &&
                                            candidate.State !=
                                                DynamicFlowStepStates.Completed &&
                                            candidate.State !=
                                                DynamicFlowStepStates
                                                    .CancelledByGateway,
                                        Builders<DynamicFlowStepInstance>.Update
                                            .Set(
                                                candidate => candidate.State,
                                                DynamicFlowStepStates
                                                    .CancelledByGateway)
                                            .Set(
                                                candidate => candidate.UpdatedAtUtc,
                                                now)
                                            .Set(
                                                candidate => candidate.UpdatedByUserId,
                                                actorUserId)
                                            .Inc(candidate => candidate.Revision, 1),
                                        cancellationToken: transactionCt);
                                if (cancelledStepsResult.ModifiedCount !=
                                    cancelledSiblingSteps.Count)
                                {
                                    throw new InvalidOperationException(
                                        "DYNAMIC_FLOW_JOIN_CANCEL_CAS_CONFLICT");
                                }
                                var cancelledAssignmentsResult =
                                    await _ctx.WorkAssignments.UpdateManyAsync(
                                        session,
                                        candidate =>
                                            cancelledAssignments.Contains(candidate.Id) &&
                                            candidate.FlowEffectiveStatus ==
                                                DynamicFlowEffectiveStatuses.Effective,
                                        Builders<WorkAssignment>.Update
                                            .Set(
                                                candidate =>
                                                    candidate.FlowEffectiveStatus,
                                                DynamicFlowEffectiveStatuses.Invalidated)
                                            .Set(
                                                candidate =>
                                                    candidate.InvalidatedByFlowEventId,
                                                joinSatisfiedEventId)
                                            .Set(candidate => candidate.UpdatedAtUtc, now),
                                        cancellationToken: transactionCt);
                                if (cancelledAssignmentsResult.ModifiedCount !=
                                    cancelledSiblingSteps.Count)
                                {
                                    throw new InvalidOperationException(
                                        "DYNAMIC_FLOW_JOIN_CANCEL_ASSIGNMENT_CONFLICT");
                                }
                            }
                            _faults.ThrowIfConfigured(
                                sourceCommandId,
                                DynamicFlowRuntimeStateProjectionFaultPoints
                                    .AfterJoinGatewayWrite);
                        }

                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeInstanceWrite);
                        var instanceUpdate = Builders<DynamicFlowInstance>.Update
                            .Inc(x => x.NextEventSequence, eventCount)
                            .Inc(x => x.Revision, 1)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId);
                        if (willCompleteInstance)
                        {
                            DynamicFlowRuntimeStateContract.RequireInstanceTransition(
                                instance.State,
                                DynamicFlowInstanceStates.Completed);
                            instanceUpdate = instanceUpdate
                                .Set(x => x.State, DynamicFlowInstanceStates.Completed)
                                .Set(x => x.CompletedAtUtc, now);
                        }
                        var instanceUpdateResult = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.InstanceCas(
                                instance.Id,
                                instance.Revision,
                                instance.State),
                            instanceUpdate,
                            cancellationToken: transactionCt);
                        if (instanceUpdateResult.ModifiedCount != 1)
                            throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.AfterInstanceWrite);

                        var visibleUnits = new[] { step.TargetUnitId, instance.IssuerUnitId }
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList();
                        var affectedRefs = new List<string>
                        {
                            $"assignment:{assignment.Id}",
                            $"step:{step.Id}"
                        };
                        if (joinContributionEligible)
                        {
                            affectedRefs.Add(
                                $"gateway:{joinGateway!.GatewayInstanceId}");
                            affectedRefs.Add(
                                $"contribution:{step.ContributionId}");
                        }
                        // Lifecycle events remain owned by the append-only source
                        // report/entry. The projected step metadata may instead
                        // point at a newer active/current period authority.
                        var eventReportId = commandType ==
                                            DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle
                            ? sourceReportId
                            : effectiveReportId ?? sourceReportId;
                        if (!string.IsNullOrWhiteSpace(eventReportId))
                            affectedRefs.Add($"report:{eventReportId}");
                        var runtimeEvents = transitions
                            .Select((transition, index) =>
                            {
                                var payload = new BsonDocument
                                {
                                    { "assignmentId", assignment.Id },
                                    { "executionEpoch", step.ExecutionEpoch },
                                    { "definitionRevision", step.DefinitionRevision },
                                    { "flowStepId", step.FlowStepId },
                                    { "branchId", step.BranchId },
                                    { "attemptNo", step.AttemptNo },
                                    { "sourceEventKey", sourceEventKey },
                                    { "sourceCommandId", sourceCommandId },
                                    { "sourceReportId", BsonValue.Create(eventReportId) },
                                    { "sourceLifecycleRevision", BsonValue.Create(sourceLifecycleRevision) },
                                    { "sourceLifecycleStatus", BsonValue.Create(sourceLifecycleStatus) },
                                    { "sourceLifecycleIsActive", BsonValue.Create(sourceLifecycleIsActive) },
                                    { "fromState", transition.From },
                                    { "toState", transition.To },
                                    { "reasonCode", reasonCode },
                                    { "projectionFingerprint", effectiveFingerprint }
                                };
                                return new DynamicFlowRuntimeEvent
                                {
                                    Id = eventIds[index],
                                    FlowInstanceId = instance.Id,
                                    StepInstanceId = step.Id,
                                    ExecutionEpoch = step.ExecutionEpoch,
                                    BranchId = step.BranchId,
                                    AttemptNo = step.AttemptNo,
                                    Sequence = eventSequenceStart + index,
                                    EventType = transition.From == transition.To
                                        ? commandType switch
                                        {
                                            DynamicFlowRuntimeStateProjectionOperations.AssignmentComplete =>
                                                DynamicFlowRuntimeStateProjectionEventTypes.AssignmentCompletionProjected,
                                            DynamicFlowRuntimeStateProjectionOperations.StateReconcile =>
                                                DynamicFlowRuntimeStateProjectionEventTypes.StateReconciled,
                                            _ => DynamicFlowRuntimeStateProjectionEventTypes.ReportLifecycleProjected
                                        }
                                        : DynamicFlowRuntimeStateProjectionEventTypes.StepStateChanged,
                                    CommandId = sourceEventKey,
                                    CorrelationId = sourceCommandId,
                                    SourceEventKey = sourceEventKey,
                                    FromState = transition.From,
                                    ToState = transition.To,
                                    FromRevision = stepFromRevision + index,
                                    ToRevision = stepFromRevision + index + 1,
                                    ReasonCode = reasonCode,
                                    AffectedRefs = affectedRefs,
                                    ActorUserId = actorUserId,
                                    VisibleUnitIds = visibleUnits,
                                    Payload = payload,
                                    PayloadHash = Hash(payload.ToJson()),
                                    OccurredAtUtc = now
                                };
                            })
                            .ToList();
                        var joinSequenceOffset = transitions.Length;
                        if (joinContributionEligible &&
                            joinContributionEventId is not null)
                        {
                            var missing = joinGateway!.ExpectedContributionIds
                                .Except(
                                    joinArrivedAfter!,
                                    StringComparer.Ordinal)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToArray();
                            var payload = new BsonDocument
                            {
                                { "assignmentId", assignment.Id },
                                { "stepInstanceId", step.Id },
                                { "branchId", step.BranchId },
                                {
                                    "gatewayInstanceId",
                                    joinGateway.GatewayInstanceId
                                },
                                {
                                    "gatewayVersion",
                                    joinGateway.GatewayVersion
                                },
                                {
                                    "contributionId",
                                    step.ContributionId
                                },
                                {
                                    "expectedContributionIds",
                                    new BsonArray(
                                        joinGateway.ExpectedContributionIds
                                            .OrderBy(
                                                value => value,
                                                StringComparer.Ordinal))
                                },
                                {
                                    "arrivedContributionIds",
                                    new BsonArray(joinArrivedAfter!)
                                },
                                {
                                    "missingContributionIds",
                                    new BsonArray(missing)
                                },
                                {
                                    "gatewayState",
                                    willSatisfyJoin
                                        ? DynamicFlowGatewayStates.Satisfied
                                        : DynamicFlowGatewayStates.Collecting
                                }
                            };
                            runtimeEvents.Add(new DynamicFlowRuntimeEvent
                            {
                                Id = joinContributionEventId,
                                FlowInstanceId = instance.Id,
                                StepInstanceId = step.Id,
                                ExecutionEpoch = step.ExecutionEpoch,
                                BranchId = step.BranchId,
                                AttemptNo = step.AttemptNo,
                                GatewayInstanceId =
                                    joinGateway.GatewayInstanceId,
                                GatewayVersion = joinGateway.GatewayVersion,
                                ContributionId = step.ContributionId,
                                Sequence =
                                    eventSequenceStart + joinSequenceOffset,
                                EventType = DynamicFlowRuntimeStateProjectionEventTypes
                                    .JoinContributionAccepted,
                                CommandId = sourceEventKey,
                                CorrelationId = sourceCommandId,
                                SourceEventKey = sourceEventKey,
                                FromState = DynamicFlowGatewayStates.Collecting,
                                ToState = DynamicFlowGatewayStates.Collecting,
                                ReasonCode = "EFFECTIVE_BRANCH_COMPLETED",
                                AffectedRefs = affectedRefs,
                                ActorUserId = actorUserId,
                                VisibleUnitIds = visibleUnits,
                                Payload = payload,
                                PayloadHash = Hash(payload.ToJson()),
                                OccurredAtUtc = now
                            });
                            joinSequenceOffset++;
                        }
                        if (joinContributionEligible &&
                            joinLateEventId is not null)
                        {
                            var lateContributionIds = joinGateway!
                                .LateContributionIds
                                .Append(step.ContributionId!)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToArray();
                            var payload = new BsonDocument
                            {
                                { "assignmentId", assignment.Id },
                                { "stepInstanceId", step.Id },
                                { "branchId", step.BranchId },
                                {
                                    "gatewayInstanceId",
                                    joinGateway.GatewayInstanceId
                                },
                                {
                                    "gatewayVersion",
                                    joinGateway.GatewayVersion
                                },
                                {
                                    "contributionId",
                                    step.ContributionId
                                },
                                {
                                    "winnerContributionId",
                                    BsonValue.Create(
                                        joinGateway.WinnerContributionId)
                                },
                                {
                                    "arrivedContributionIds",
                                    new BsonArray(joinArrivedAfter!)
                                },
                                {
                                    "cancelledContributionIds",
                                    new BsonArray(
                                        joinGateway.CancelledContributionIds)
                                },
                                {
                                    "lateContributionIds",
                                    new BsonArray(lateContributionIds)
                                },
                                {
                                    "gatewayState",
                                    DynamicFlowGatewayStates.Satisfied
                                }
                            };
                            runtimeEvents.Add(new DynamicFlowRuntimeEvent
                            {
                                Id = joinLateEventId,
                                FlowInstanceId = instance.Id,
                                StepInstanceId = step.Id,
                                ExecutionEpoch = step.ExecutionEpoch,
                                BranchId = step.BranchId,
                                AttemptNo = step.AttemptNo,
                                GatewayInstanceId =
                                    joinGateway.GatewayInstanceId,
                                GatewayVersion = joinGateway.GatewayVersion,
                                ContributionId = step.ContributionId,
                                Sequence =
                                    eventSequenceStart + joinSequenceOffset,
                                EventType = DynamicFlowRuntimeStateProjectionEventTypes
                                    .JoinLateIgnored,
                                CommandId = sourceEventKey,
                                CorrelationId = sourceCommandId,
                                SourceEventKey = sourceEventKey,
                                FromState = DynamicFlowGatewayStates.Satisfied,
                                ToState = DynamicFlowGatewayStates.Satisfied,
                                FromRevision = joinGateway.Revision,
                                ToRevision = joinGateway.Revision + 1,
                                ReasonCode = "LATE_IGNORED",
                                AffectedRefs = affectedRefs,
                                ActorUserId = actorUserId,
                                VisibleUnitIds = visibleUnits,
                                Payload = payload,
                                PayloadHash = Hash(payload.ToJson()),
                                OccurredAtUtc = now
                            });
                            joinSequenceOffset++;
                        }
                        if (willSatisfyJoin &&
                            joinSatisfiedEventId is not null)
                        {
                            var payload = new BsonDocument
                            {
                                {
                                    "gatewayInstanceId",
                                    joinGateway!.GatewayInstanceId
                                },
                                {
                                    "gatewayVersion",
                                    joinGateway.GatewayVersion
                                },
                                {
                                    "expectedContributionIds",
                                    new BsonArray(
                                        joinGateway.ExpectedContributionIds
                                            .OrderBy(
                                                value => value,
                                                StringComparer.Ordinal))
                                },
                                {
                                    "arrivedContributionIds",
                                    new BsonArray(joinArrivedAfter!)
                                },
                                {
                                    "requiredContributionCount",
                                    requiredContributionCount
                                },
                                {
                                    "winnerContributionId",
                                    step.ContributionId
                                },
                                {
                                    "cancelledContributionIds",
                                    new BsonArray(cancelledContributionIds)
                                },
                                {
                                    "downstreamNodeId",
                                    joinGateway.DownstreamNodeId
                                },
                                {
                                    "fromState",
                                    DynamicFlowGatewayStates.Collecting
                                },
                                {
                                    "toState",
                                    DynamicFlowGatewayStates.Satisfied
                                }
                            };
                            runtimeEvents.Add(new DynamicFlowRuntimeEvent
                            {
                                Id = joinSatisfiedEventId,
                                FlowInstanceId = instance.Id,
                                StepInstanceId = null,
                                ExecutionEpoch = step.ExecutionEpoch,
                                BranchId = null,
                                AttemptNo = null,
                                GatewayInstanceId =
                                    joinGateway.GatewayInstanceId,
                                GatewayVersion = joinGateway.GatewayVersion,
                                ContributionId = null,
                                Sequence =
                                    eventSequenceStart + joinSequenceOffset,
                                EventType = isQuorumJoin
                                    ? DynamicFlowRuntimeStateProjectionEventTypes
                                        .JoinQuorumSatisfied
                                    : DynamicFlowRuntimeStateProjectionEventTypes
                                        .JoinAllSatisfied,
                                CommandId = sourceEventKey,
                                CorrelationId = sourceCommandId,
                                SourceEventKey = sourceEventKey,
                                FromState = DynamicFlowGatewayStates.Collecting,
                                ToState = DynamicFlowGatewayStates.Satisfied,
                                FromRevision = joinGateway.Revision,
                                ToRevision = joinGateway.Revision + 1,
                                ReasonCode = isQuorumJoin
                                    ? "QUORUM_REACHED"
                                    : "ALL_EXPECTED_CONTRIBUTIONS_ARRIVED",
                                AffectedRefs = affectedRefs,
                                ActorUserId = actorUserId,
                                VisibleUnitIds = visibleUnits,
                                Payload = payload,
                                PayloadHash = Hash(payload.ToJson()),
                                OccurredAtUtc = now
                            });
                            joinSequenceOffset++;
                        }
                        for (var index = 0;
                             index < cancelledSiblingSteps.Count;
                             index++)
                        {
                            var cancelledStep = cancelledSiblingSteps[index];
                            var cancelledEventId = joinCancelledEventIds[index];
                            var cancelledRefs = affectedRefs
                                .Append($"step:{cancelledStep.Id}")
                                .Append($"assignment:{cancelledStep.AssignmentId}")
                                .Distinct(StringComparer.Ordinal)
                                .ToList();
                            var cancelledVisibleUnits = visibleUnits
                                .Append(cancelledStep.TargetUnitId)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToList();
                            var payload = new BsonDocument
                            {
                                {
                                    "gatewayInstanceId",
                                    joinGateway!.GatewayInstanceId
                                },
                                {
                                    "gatewayVersion",
                                    joinGateway.GatewayVersion
                                },
                                {
                                    "winnerContributionId",
                                    step.ContributionId
                                },
                                {
                                    "cancelledContributionId",
                                    cancelledStep.ContributionId
                                },
                                {
                                    "cancelledStepInstanceId",
                                    cancelledStep.Id
                                },
                                {
                                    "cancelledAssignmentId",
                                    cancelledStep.AssignmentId
                                },
                                {
                                    "fromState",
                                    cancelledStep.State
                                },
                                {
                                    "toState",
                                    DynamicFlowStepStates.CancelledByGateway
                                }
                            };
                            runtimeEvents.Add(new DynamicFlowRuntimeEvent
                            {
                                Id = cancelledEventId,
                                FlowInstanceId = instance.Id,
                                StepInstanceId = cancelledStep.Id,
                                ExecutionEpoch = cancelledStep.ExecutionEpoch,
                                BranchId = cancelledStep.BranchId,
                                AttemptNo = cancelledStep.AttemptNo,
                                GatewayInstanceId =
                                    joinGateway.GatewayInstanceId,
                                GatewayVersion = joinGateway.GatewayVersion,
                                ContributionId =
                                    cancelledStep.ContributionId,
                                Sequence =
                                    eventSequenceStart + joinSequenceOffset,
                                EventType = DynamicFlowRuntimeStateProjectionEventTypes
                                    .JoinBranchCancelled,
                                CommandId = sourceEventKey,
                                CorrelationId = sourceCommandId,
                                SourceEventKey = sourceEventKey,
                                FromState = cancelledStep.State,
                                ToState =
                                    DynamicFlowStepStates.CancelledByGateway,
                                FromRevision = cancelledStep.Revision,
                                ToRevision = cancelledStep.Revision + 1,
                                ReasonCode = "CANCELLED_BY_GATEWAY",
                                AffectedRefs = cancelledRefs,
                                ActorUserId = actorUserId,
                                VisibleUnitIds = cancelledVisibleUnits,
                                Payload = payload,
                                PayloadHash = Hash(payload.ToJson()),
                                OccurredAtUtc = now
                            });
                            joinSequenceOffset++;
                        }
                        if (willCompleteInstance && instanceEventId is not null)
                        {
                            var instanceCompletionReason =
                                joinContributionEligible
                                    ? isQuorumJoin
                                        ? "JOIN_QUORUM_SATISFIED"
                                        : "JOIN_ALL_SATISFIED"
                                    : "ALL_RUNTIME_STEPS_COMPLETED";
                            var payload = new BsonDocument
                            {
                                { "assignmentId", assignment.Id },
                                { "terminalStepInstanceId", step.Id },
                                { "executionEpoch", step.ExecutionEpoch },
                                { "definitionRevision", step.DefinitionRevision },
                                { "flowStepId", step.FlowStepId },
                                { "branchId", step.BranchId },
                                { "attemptNo", step.AttemptNo },
                                { "sourceEventKey", sourceEventKey },
                                { "sourceReportId", BsonValue.Create(eventReportId) },
                                { "fromState", instance.State },
                                { "toState", DynamicFlowInstanceStates.Completed },
                                {
                                    "reasonCode",
                                    instanceCompletionReason
                                }
                            };
                            runtimeEvents.Add(new DynamicFlowRuntimeEvent
                            {
                                Id = instanceEventId,
                                FlowInstanceId = instance.Id,
                                StepInstanceId = null,
                                ExecutionEpoch = step.ExecutionEpoch,
                                BranchId = step.BranchId,
                                AttemptNo = step.AttemptNo,
                                Sequence =
                                    eventSequenceStart + joinSequenceOffset,
                                EventType = DynamicFlowRuntimeStateProjectionEventTypes.InstanceCompleted,
                                CommandId = sourceEventKey,
                                CorrelationId = sourceCommandId,
                                SourceEventKey = sourceEventKey,
                                FromState = instance.State,
                                ToState = DynamicFlowInstanceStates.Completed,
                                FromRevision = instanceFromRevision,
                                ToRevision = instanceToRevision,
                                ReasonCode = instanceCompletionReason,
                                AffectedRefs = affectedRefs,
                                ActorUserId = actorUserId,
                                VisibleUnitIds = visibleUnits,
                                Payload = payload,
                                PayloadHash = Hash(payload.ToJson()),
                                OccurredAtUtc = now
                            });
                        }

                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeEventWrite);
                        await _ctx.DynamicFlowRuntimeEvents.InsertManyAsync(
                            session,
                            runtimeEvents,
                            cancellationToken: transactionCt);
                        _faults.ThrowIfConfigured(
                            sourceCommandId,
                            DynamicFlowRuntimeStateProjectionFaultPoints.AfterEventWrite);
                        await WorkDirectSourceRevisionFence.IncrementAsync(
                            _ctx,
                            session,
                            instance.WorkId,
                            transactionCt);

                        return new DynamicFlowRuntimeStateProjectionResult(
                            instance.Id,
                            step.Id,
                            assignment.Id,
                            commandType,
                            sourceCommandId,
                            sourceEventKey,
                            step.State,
                            effectiveTarget,
                            stepFromRevision,
                            stepToRevision,
                            instanceFromRevision,
                            instanceToRevision,
                            runtimeEvents.Select(item => item.Id).ToArray(),
                            Replayed: false,
                            InstanceCompleted: willCompleteInstance,
                            ProjectionFingerprint: effectiveFingerprint);
                    },
                    ct);
                _faults.ThrowIfConfigured(
                    sourceCommandId,
                    DynamicFlowRuntimeStateProjectionFaultPoints.AfterTransactionCommit);
                return committed;
            }
            catch (Exception error) when (
                IsRetryableRace(error) &&
                attempt < MaxRaceAttempts &&
                !ct.IsCancellationRequested)
            {
                // Deterministic receipts turn duplicate, CAS and transient transaction races
                // into a bounded replay/re-evaluation on the next attempt.
            }
        }

        throw new InvalidOperationException("DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_RACE_EXHAUSTED");
    }

    private async Task<DynamicFlowRuntimeStateProjectionResult?> TryReadProjectionReplayAsync(
        string receiptId,
        string commandType,
        string sourceEventKey,
        string requestHash,
        CancellationToken ct)
    {
        var receipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        return receipt is null
            ? null
            : ParseProjectionReplay(receipt, commandType, sourceEventKey, requestHash);
    }

    private static DynamicFlowRuntimeStateProjectionResult ParseProjectionReplay(
        DynamicFlowRuntimeCommandReceipt receipt,
        string commandType,
        string sourceEventKey,
        string requestHash)
    {
        if (receipt.CommandType != commandType ||
            receipt.CommandId != sourceEventKey ||
            receipt.RequestHash != requestHash ||
            receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.ResultSnapshot is null ||
            receipt.ResultSnapshotHash != Hash(receipt.ResultSnapshot.ToJson()))
        {
            throw new InvalidOperationException("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT");
        }

        var result = receipt.ResultSnapshot;
        return new DynamicFlowRuntimeStateProjectionResult(
            RequiredResultString(result, "flowInstanceId"),
            RequiredResultString(result, "stepInstanceId"),
            RequiredResultString(result, "assignmentId"),
            RequiredResultString(result, "commandType"),
            RequiredResultString(result, "sourceCommandId"),
            RequiredResultString(result, "sourceEventKey"),
            RequiredResultString(result, "fromState"),
            RequiredResultString(result, "toState"),
            result["fromStepRevision"].ToInt64(),
            result["toStepRevision"].ToInt64(),
            result["fromInstanceRevision"].ToInt64(),
            result["toInstanceRevision"].ToInt64(),
            result["eventIds"].AsBsonArray.Select(value => value.AsString).ToArray(),
            Replayed: true,
            InstanceCompleted: result["instanceCompleted"].AsBoolean,
            ProjectionFingerprint: RequiredResultString(result, "projectionFingerprint"));
    }

    private async Task<ReportProjection> ResolveReportProjectionAsync(
        IClientSessionHandle? session,
        WorkAssignment assignment,
        CancellationToken ct,
        string? overrideReportId = null,
        int? overrideLifecycleRevision = null,
        string? overrideLifecycleStatus = null,
        bool? overrideLifecycleIsActive = null,
        string? overrideLifecycleEntryKey = null,
        string? overrideLifecycleCommandId = null,
        string? overrideLifecycleOperation = null)
    {
        List<WorkReportPeriod> periods;
        List<WorkAssignmentReport> reports;
        if (session is null)
        {
            periods = await _ctx.WorkReportPeriods
                .Find(x =>
                    x.WorkAssignmentId == assignment.Id &&
                    x.IsActive &&
                    !x.IsDeleted)
                .ToListAsync(ct);
            reports = await _ctx.WorkAssignmentReports
                .Find(x =>
                    x.WorkAssignmentId == assignment.Id &&
                    !x.IsDeleted)
                .ToListAsync(ct);
        }
        else
        {
            periods = await _ctx.WorkReportPeriods
                .Find(
                    session,
                    x =>
                        x.WorkAssignmentId == assignment.Id &&
                        x.IsActive &&
                        !x.IsDeleted)
                .ToListAsync(ct);
            reports = await _ctx.WorkAssignmentReports
                .Find(
                    session,
                    x =>
                        x.WorkAssignmentId == assignment.Id &&
                        !x.IsDeleted)
                .ToListAsync(ct);
        }

        var expectedPeriodIds = periods
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var authoritative = reports
            .Where(report => expectedPeriodIds.Contains(report.WorkReportPeriodId, StringComparer.Ordinal))
            .GroupBy(report => report.WorkReportPeriodId, StringComparer.Ordinal)
            .Select(group => group
                // A newer active/current report is the period authority even when
                // an older report's delayed outbox entry is being retried. The
                // trigger remains authoritative only when deactivation left the
                // period without an active/current report.
                .OrderByDescending(report => report.IsCurrent && report.IsActive)
                .ThenByDescending(report =>
                    !string.IsNullOrWhiteSpace(overrideReportId) &&
                    string.Equals(report.Id, overrideReportId, StringComparison.Ordinal))
                .ThenByDescending(report => report.IsCurrent)
                .ThenByDescending(report => report.VersionNo)
                .ThenByDescending(report => report.UpdatedAtUtc)
                .ThenByDescending(report => report.Id, StringComparer.Ordinal)
                .First())
            .OrderBy(report => report.WorkReportPeriodId, StringComparer.Ordinal)
            .ToArray();
        var projectionSources = authoritative
            .Select(report =>
            {
                var parsedOverrideStatus = report.Status;
                var overrideStatusValid =
                    System.Enum.TryParse<WorkAssignmentReportStatus>(
                        overrideLifecycleStatus,
                        ignoreCase: true,
                        out parsedOverrideStatus);
                var isOverride =
                    !string.IsNullOrWhiteSpace(overrideReportId) &&
                    string.Equals(report.Id, overrideReportId, StringComparison.Ordinal) &&
                    overrideLifecycleRevision.HasValue &&
                    overrideStatusValid;
                var lifecycleRevision = isOverride
                    ? overrideLifecycleRevision!.Value
                    : report.LifecycleRevision;
                var lifecycleStatus = isOverride
                    ? parsedOverrideStatus
                    : report.Status;
                var lifecycleIsActive = isOverride
                    ? overrideLifecycleIsActive ?? report.IsActive
                    : report.IsActive;
                var lifecycleEntry = isOverride
                    ? (report.LifecycleProjectionOutbox ??
                            new List<WorkReportLifecycleProjectionOutboxEntry>())
                        .FirstOrDefault(entry =>
                            entry.LifecycleRevision == lifecycleRevision &&
                            string.Equals(
                                entry.EntryKey,
                                overrideLifecycleEntryKey,
                                StringComparison.Ordinal))
                    : (report.LifecycleProjectionOutbox ??
                            new List<WorkReportLifecycleProjectionOutboxEntry>())
                        .Where(entry => entry.LifecycleRevision == lifecycleRevision)
                        .OrderBy(entry => entry.CreatedAtUtc)
                        .ThenBy(entry => entry.EntryKey, StringComparer.Ordinal)
                        .LastOrDefault();
                var entryKey = isOverride
                    ? overrideLifecycleEntryKey
                    : lifecycleEntry?.EntryKey;
                var commandId = isOverride
                    ? overrideLifecycleCommandId
                    : lifecycleEntry?.CommandId;
                var isReturnedDraft =
                    lifecycleStatus == WorkAssignmentReportStatus.Draft &&
                    (isOverride
                        ? (overrideLifecycleOperation ?? string.Empty)
                            .Contains("RETURN", StringComparison.OrdinalIgnoreCase)
                        : report.ReturnedAtUtc.HasValue);
                return new ReportProjectionSource(
                    report.Id,
                    report.WorkReportPeriodId,
                    lifecycleRevision,
                    lifecycleStatus,
                    lifecycleIsActive,
                    isReturnedDraft,
                    entryKey,
                    commandId,
                    lifecycleEntry?.CreatedAtUtc ?? DateTime.MinValue);
            })
            .OrderBy(report => report.PeriodId, StringComparer.Ordinal)
            .ToArray();
        var target = ResolveReportTargetState(
            expectedPeriodIds.Length,
            projectionSources);
        var fingerprint = Hash(string.Join(
            "\n",
            assignment.Id,
            target,
            string.Join(
                "|",
                expectedPeriodIds),
            string.Join(
                "|",
                projectionSources.Select(report =>
                    $"{report.ReportId}:{report.LifecycleRevision}:{report.Status}:{(report.IsActive ? 1 : 0)}:{report.EntryKey}:{report.CommandId}"))));
        var canonicalReport = projectionSources.Length == 1
            ? projectionSources[0]
            : null;
        var canonicalLifecycle = projectionSources
            .Where(item => !string.IsNullOrWhiteSpace(item.EntryKey))
            .OrderBy(item => item.LifecycleRevision)
            .ThenBy(item => item.EntryCreatedAtUtc)
            .ThenBy(item => item.ReportId, StringComparer.Ordinal)
            .ThenBy(item => item.EntryKey, StringComparer.Ordinal)
            .LastOrDefault();
        return new ReportProjection(
            target,
            fingerprint,
            canonicalReport?.ReportId,
            canonicalLifecycle?.EntryKey,
            canonicalLifecycle?.CommandId,
            projectionSources.Length == 0
                ? null
                : projectionSources.Max(report => report.LifecycleRevision),
            projectionSources.Length == 0
                ? null
                : canonicalReport?.Status.ToString().ToUpperInvariant() ?? target,
            projectionSources.Length == 0
                ? null
                : projectionSources.All(report => report.IsActive));
    }

    private static (string From, string To)[] BuildTransitions(
        string initial,
        IReadOnlyList<string> path)
    {
        var transitions = new List<(string From, string To)>(path.Count);
        var current = initial;
        foreach (var next in path)
        {
            DynamicFlowRuntimeStateContract.RequireStepTransition(current, next);
            transitions.Add((current, next));
            current = next;
        }
        return transitions.ToArray();
    }

    private async Task<bool> IsT01SoleStepAsync(
        IClientSessionHandle? session,
        string flowInstanceId,
        string stepInstanceId,
        IReadOnlyCollection<DynamicFlowStepInstance> steps,
        CancellationToken ct)
    {
        if (steps.Count != 1 ||
            !string.Equals(steps.Single().Id, stepInstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        return await ResolveStepArchetypeAsync(
                   session,
                   flowInstanceId,
                   stepInstanceId,
                   ct) == "FLOW-T01";
    }

    private async Task<string?> ResolveStepArchetypeAsync(
        IClientSessionHandle? session,
        string flowInstanceId,
        string stepInstanceId,
        CancellationToken ct)
    {
        List<DynamicFlowRuntimeOutboxItem> intents;
        if (session is null)
        {
            intents = await _ctx.DynamicFlowRuntimeOutbox
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    x.StepInstanceId == stepInstanceId &&
                    (x.Operation ==
                     DynamicFlowRuntimeMaterializationOperations.MaterializeEntryAssignment ||
                     x.Operation ==
                     DynamicFlowRuntimeMaterializationOperations.MaterializeSequentialAssignment ||
                     x.Operation ==
                     DynamicFlowRuntimeMaterializationOperations.MaterializeForkBranchAssignment ||
                     x.Operation ==
                     DynamicFlowRuntimeMaterializationOperations.MaterializeReviewAttemptAssignment))
                .ToListAsync(ct);
        }
        else
        {
            intents = await _ctx.DynamicFlowRuntimeOutbox
                .Find(
                    session,
                    x =>
                        x.FlowInstanceId == flowInstanceId &&
                        x.StepInstanceId == stepInstanceId &&
                        (x.Operation ==
                         DynamicFlowRuntimeMaterializationOperations.MaterializeEntryAssignment ||
                         x.Operation ==
                         DynamicFlowRuntimeMaterializationOperations.MaterializeSequentialAssignment ||
                         x.Operation ==
                         DynamicFlowRuntimeMaterializationOperations.MaterializeForkBranchAssignment ||
                         x.Operation ==
                         DynamicFlowRuntimeMaterializationOperations.MaterializeReviewAttemptAssignment))
                .ToListAsync(ct);
        }

        return intents.Count == 1 &&
               intents[0].Payload.TryGetValue("archetypeId", out var archetype) &&
               archetype.IsString &&
               archetype.AsString is
                   "FLOW-T01" or
                   "FLOW-T02" or
                   "FLOW-T03" or
                   "FLOW-T04" or
                   "FLOW-T05" or
                   "FLOW-T06" or
                   "FLOW-T07" or
                   "FLOW-T08" or
                   "FLOW-T09" or
                   "FLOW-T11" or
                   "FLOW-T12"
            ? archetype.AsString
            : null;
    }

    private static DynamicFlowParallelForkTopology RequireParallelFanOutTopology(
        DynamicFlowInstance instance)
        => instance.ArchetypeId switch
        {
            DynamicFlowParallelForkTopologyContract.ArchetypeId =>
                DynamicFlowParallelForkTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            DynamicFlowJoinAllTopologyContract.ArchetypeId =>
                DynamicFlowJoinAllTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId =>
                DynamicFlowJoinQuorumTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId =>
                DynamicFlowTypedConditionalTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            _ => throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_OWNERSHIP_DRIFT")
        };

    private static string ResolveActorUserId(
        WorkAssignmentReport report,
        WorkReportLifecycleProjectionOutboxEntry entry)
    {
        foreach (var candidate in new[]
                 {
                     entry.ActorUserId,
                     report.UpdatedByUserId,
                     report.CreatedByUserId,
                     report.AssigneeUserId
                 })
        {
            if (ObjectId.TryParse(candidate, out _))
                return candidate!;
        }
        throw new InvalidOperationException(
            "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_ACTOR_MISSING");
    }

    private static string Required(string? value, string field)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{field} is required.", field)
            : value;
    }

    private static string RequiredObjectId(string? value, string field)
    {
        value = Required(value, field);
        return ObjectId.TryParse(value, out _)
            ? value
            : throw new ArgumentException($"{field} must be an ObjectId.", field);
    }

    private static string RequiredResultString(BsonDocument result, string field)
        => result.TryGetValue(field, out var value) && value.IsString
            ? value.AsString
            : throw new InvalidOperationException(
                $"DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_RESULT_INVALID:{field}");

    private static string NormalizeReason(string? value)
    {
        value = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return "UNKNOWN";
        var normalized = new string(value
            .Select(character =>
                char.IsAsciiLetterOrDigit(character) || character == '_'
                    ? character
                    : '_')
            .ToArray());
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static string StableObjectId(string seed)
        => Hash(seed)[..24];

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsDuplicateKey(Exception error)
    {
        if (error is MongoWriteException write &&
            write.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return true;
        }
        return IsDuplicateKeyRemainder(error);
    }

    public async Task ProjectSubflowParentTerminalAsync(
        string childInstanceId,
        string actorUserId,
        CancellationToken ct = default)
    {
        childInstanceId = RequiredObjectId(
            childInstanceId,
            nameof(childInstanceId));
        actorUserId = RequiredObjectId(
            actorUserId,
            nameof(actorUserId));
        var child = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == childInstanceId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (child is null ||
            string.IsNullOrWhiteSpace(child.ParentInstanceId) ||
            string.IsNullOrWhiteSpace(child.ParentStepInstanceId) ||
            child.State is not (
                DynamicFlowInstanceStates.Completed or
                DynamicFlowInstanceStates.Failed or
                DynamicFlowInstanceStates.Terminated))
        {
            return;
        }

        var commandId =
            $"child-terminal:{child.Id}:{child.Revision}:{child.State}";
        var receiptId = StableObjectId(
            $"{DynamicFlowCommandScopeKinds.Instance}\n{child.ParentInstanceId}\n{DynamicFlowRuntimeStateProjectionOperations.SubflowChildTerminal}\n{commandId}");
        if (await _ctx.DynamicFlowRuntimeCommandReceipts
                .CountDocumentsAsync(
                    candidate =>
                        candidate.Id == receiptId &&
                        candidate.Status ==
                            DynamicFlowRuntimeCommandStatuses.Succeeded,
                    cancellationToken: ct) == 1)
        {
            return;
        }

        string? terminalParentInstanceId = null;
        await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var existing =
                    await _ctx.DynamicFlowRuntimeCommandReceipts
                        .Find(session, candidate =>
                            candidate.Id == receiptId)
                        .FirstOrDefaultAsync(transactionCt);
                if (existing is not null)
                {
                    if (existing.Status !=
                            DynamicFlowRuntimeCommandStatuses.Succeeded ||
                        existing.CommandType !=
                            DynamicFlowRuntimeStateProjectionOperations
                                .SubflowChildTerminal ||
                        existing.CommandId != commandId)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_SUBFLOW_TERMINAL_REPLAY_CONFLICT");
                    }
                    return;
                }

                var currentChild = await _ctx.DynamicFlowInstances
                    .Find(session, candidate =>
                        candidate.Id == child.Id &&
                        candidate.ParentInstanceId ==
                            child.ParentInstanceId &&
                        candidate.ParentStepInstanceId ==
                            child.ParentStepInstanceId &&
                        candidate.State == child.State &&
                        candidate.Revision == child.Revision &&
                        !candidate.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt);
                var parent = await _ctx.DynamicFlowInstances
                    .Find(session, candidate =>
                        candidate.Id == child.ParentInstanceId &&
                        candidate.ArchetypeId ==
                            DynamicFlowSubflowTopologyContract.ArchetypeId &&
                        candidate.State ==
                            DynamicFlowInstanceStates.Active &&
                        !candidate.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt);
                var parentStep =
                    await _ctx.DynamicFlowStepInstances
                        .Find(session, candidate =>
                            candidate.Id ==
                                child.ParentStepInstanceId &&
                            candidate.FlowInstanceId ==
                                child.ParentInstanceId &&
                            candidate.ChildInstanceId == child.Id &&
                            candidate.ChildFlowTemplateId ==
                                child.FlowTemplateId &&
                            candidate.ChildFlowVersionId ==
                                child.FlowTemplateVersionId &&
                            candidate.State ==
                                DynamicFlowStepStates.WaitingChild &&
                            !candidate.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                if (currentChild is null ||
                    parent is null ||
                    parentStep is null)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_SUBFLOW_TERMINAL_LINEAGE_DRIFT");
                }
                if (child.AncestryPath.Count == 0 ||
                    child.AncestryPath[^1] != parent.Id ||
                    child.AncestryFlowFamilyIds.Count !=
                        child.AncestryPath.Count ||
                    child.AncestryFlowFamilyIds[^1] !=
                        parent.FlowTemplateId ||
                    child.RootInstanceId !=
                        (parent.RootInstanceId ?? parent.Id))
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_SUBFLOW_TERMINAL_LINEAGE_DRIFT");
                }

                var siblingSteps =
                    await _ctx.DynamicFlowStepInstances
                        .Find(session, candidate =>
                            candidate.FlowInstanceId == parent.Id &&
                            !candidate.IsDeleted)
                        .ToListAsync(transactionCt);
                var childSucceeded =
                    child.State ==
                    DynamicFlowInstanceStates.Completed;
                var allChildrenSucceeded =
                    childSucceeded &&
                    siblingSteps.All(candidate =>
                        candidate.Id == parentStep.Id ||
                        candidate.State ==
                            DynamicFlowStepStates.Completed);
                var parentStepState = child.State switch
                {
                    DynamicFlowInstanceStates.Completed =>
                        DynamicFlowStepStates.Completed,
                    DynamicFlowInstanceStates.Failed =>
                        DynamicFlowStepStates.Failed,
                    DynamicFlowInstanceStates.Terminated =>
                        DynamicFlowStepStates.Terminated,
                    _ => throw new InvalidOperationException(
                        "DYNAMIC_FLOW_SUBFLOW_CHILD_NOT_TERMINAL")
                };
                var parentState = child.State switch
                {
                    DynamicFlowInstanceStates.Completed
                        when allChildrenSucceeded =>
                        DynamicFlowInstanceStates.Completed,
                    DynamicFlowInstanceStates.Completed =>
                        DynamicFlowInstanceStates.Active,
                    DynamicFlowInstanceStates.Failed =>
                        DynamicFlowInstanceStates.Failed,
                    DynamicFlowInstanceStates.Terminated =>
                        DynamicFlowInstanceStates.Terminated,
                    _ => throw new InvalidOperationException(
                        "DYNAMIC_FLOW_SUBFLOW_CHILD_NOT_TERMINAL")
                };
                var outcomeCode = child.State switch
                {
                    DynamicFlowInstanceStates.Completed => "COMPLETED",
                    DynamicFlowInstanceStates.Failed =>
                        DynamicFlowSubflowTopologyContract.ChildFailed,
                    DynamicFlowInstanceStates.Terminated =>
                        DynamicFlowSubflowTopologyContract.ChildTerminated,
                    _ => string.Empty
                };
                DynamicFlowRuntimeStateContract.RequireStepTransition(
                    parentStep.State,
                    parentStepState);
                if (parentState != parent.State)
                {
                    DynamicFlowRuntimeStateContract
                        .RequireInstanceTransition(
                            parent.State,
                            parentState);
                }

                var now = DateTime.UtcNow;
                var stepEventId = StableObjectId(
                    $"{parent.Id}\nsubflow-terminal\n{child.Id}\nstep");
                var instanceEventId = parentState == parent.State
                    ? null
                    : StableObjectId(
                        $"{parent.Id}\nsubflow-terminal\n{child.Id}\ninstance");
                var eventIds = new[] { stepEventId, instanceEventId }
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .ToArray();
                var projectionFingerprint = Hash(string.Join(
                    "\n",
                    child.Id,
                    child.State,
                    child.Revision,
                    parent.Id,
                    parentStep.Id,
                    parentStep.Revision,
                    parent.Revision));
                var requestHash = Hash(string.Join(
                    "\n",
                    commandId,
                    child.ParentInstanceId,
                    child.ParentStepInstanceId,
                    child.FlowTemplateId,
                    child.FlowTemplateVersionId,
                    child.RootInstanceId,
                    string.Join(",", child.AncestryPath),
                    string.Join(
                        ",",
                        child.AncestryFlowFamilyIds)));
                var result = new BsonDocument
                {
                    { "flowInstanceId", parent.Id },
                    { "stepInstanceId", parentStep.Id },
                    {
                        "assignmentId",
                        parentStep.AssignmentId ?? string.Empty
                    },
                    {
                        "commandType",
                        DynamicFlowRuntimeStateProjectionOperations
                            .SubflowChildTerminal
                    },
                    { "sourceCommandId", commandId },
                    { "sourceEventKey", commandId },
                    { "fromState", parentStep.State },
                    { "toState", parentStepState },
                    {
                        "fromStepRevision",
                        parentStep.Revision
                    },
                    {
                        "toStepRevision",
                        parentStep.Revision + 1
                    },
                    {
                        "fromInstanceRevision",
                        parent.Revision
                    },
                    {
                        "toInstanceRevision",
                        parent.Revision + 1
                    },
                    {
                        "projectionFingerprint",
                        projectionFingerprint
                    },
                    {
                        "instanceCompleted",
                        parentState ==
                        DynamicFlowInstanceStates.Completed
                    },
                    { "childInstanceId", child.Id },
                    { "childState", child.State },
                    { "childOutcomeCode", outcomeCode },
                    { "eventIds", new BsonArray(eventIds) }
                };
                var receipt = new DynamicFlowRuntimeCommandReceipt
                {
                    Id = receiptId,
                    ScopeKind =
                        DynamicFlowCommandScopeKinds.Instance,
                    ScopeId = parent.Id,
                    WorkId = parent.WorkId,
                    FlowTemplateVersionId =
                        parent.FlowTemplateVersionId,
                    FlowInstanceId = parent.Id,
                    CommandType =
                        DynamicFlowRuntimeStateProjectionOperations
                            .SubflowChildTerminal,
                    CommandId = commandId,
                    RequestHash = requestHash,
                    CommandIdentityHash = Hash(
                        $"{actorUserId}\n{parentStep.AssignmentId}\n{commandId}\n{commandId}"),
                    SnapshotToken = Hash(
                        $"{requestHash}\n{projectionFingerprint}\n{parent.FlowPayloadHash}"),
                    ExpectedRevision = parentStep.Revision,
                    Status =
                        DynamicFlowRuntimeCommandStatuses.Succeeded,
                    ResultSnapshot = result,
                    ResultSnapshotHash = Hash(result.ToJson()),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    UpdatedByUserId = actorUserId,
                    CompletedAtUtc = now
                };
                await _ctx.DynamicFlowRuntimeCommandReceipts
                    .InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);

                var stepUpdate =
                    await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                        session,
                        DynamicFlowRuntimeRevisionContract.StepCas(
                            parentStep.Id,
                            parentStep.Revision,
                            DynamicFlowStepStates.WaitingChild),
                        Builders<DynamicFlowStepInstance>.Update
                            .Set(
                                candidate => candidate.State,
                                parentStepState)
                            .Set(
                                candidate => candidate.ChildState,
                                child.State)
                            .Set(
                                candidate =>
                                    candidate.ChildOutcomeCode,
                                outcomeCode)
                            .Set(
                                candidate => candidate.UpdatedAtUtc,
                                now)
                            .Set(
                                candidate =>
                                    candidate.UpdatedByUserId,
                                actorUserId)
                            .Inc(candidate => candidate.Revision, 1),
                        cancellationToken: transactionCt);
                if (stepUpdate.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_REVISION_CONFLICT");
                }

                var parentUpdate =
                    Builders<DynamicFlowInstance>.Update
                        .Set(
                            candidate => candidate.State,
                            parentState)
                        .Set(
                            candidate => candidate.LastErrorCode,
                            parentState is
                                DynamicFlowInstanceStates.Failed or
                                DynamicFlowInstanceStates.Terminated
                                ? outcomeCode
                                : null)
                        .Set(
                            candidate => candidate.UpdatedAtUtc,
                            now)
                        .Set(
                            candidate =>
                                candidate.UpdatedByUserId,
                            actorUserId)
                        .Inc(candidate => candidate.Revision, 1)
                        .Inc(
                            candidate =>
                                candidate.NextEventSequence,
                            eventIds.Length);
                if (parentState is
                    DynamicFlowInstanceStates.Completed or
                    DynamicFlowInstanceStates.Failed or
                    DynamicFlowInstanceStates.Terminated)
                {
                    parentUpdate = parentUpdate.Set(
                        candidate => candidate.CompletedAtUtc,
                        now);
                }
                var instanceUpdate =
                    await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            parent.Id,
                            parent.Revision,
                            DynamicFlowInstanceStates.Active),
                        parentUpdate,
                        cancellationToken: transactionCt);
                if (instanceUpdate.ModifiedCount != 1)
                {
                    throw new InvalidOperationException(
                        "DYNAMIC_FLOW_REVISION_CONFLICT");
                }

                var stepPayload = new BsonDocument
                {
                    { "assignmentId", parentStep.AssignmentId },
                    { "executionEpoch", parentStep.ExecutionEpoch },
                    {
                        "definitionRevision",
                        parentStep.DefinitionRevision
                    },
                    { "flowStepId", parentStep.FlowStepId },
                    { "branchId", parentStep.BranchId },
                    { "attemptNo", parentStep.AttemptNo },
                    { "sourceEventKey", commandId },
                    { "sourceCommandId", commandId },
                    { "fromState", parentStep.State },
                    { "toState", parentStepState },
                    {
                        "reasonCode",
                        childSucceeded
                            ? "SUBFLOW_CHILD_COMPLETED"
                            : outcomeCode
                    },
                    {
                        "projectionFingerprint",
                        projectionFingerprint
                    },
                    { "childInstanceId", child.Id },
                    { "childState", child.State },
                    { "childOutcomeCode", outcomeCode }
                };
                await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeEvent
                    {
                        Id = stepEventId,
                        FlowInstanceId = parent.Id,
                        StepInstanceId = parentStep.Id,
                        ExecutionEpoch =
                            parentStep.ExecutionEpoch,
                        BranchId = parentStep.BranchId,
                        AttemptNo = parentStep.AttemptNo,
                        Sequence = parent.NextEventSequence,
                        EventType = childSucceeded
                            ? DynamicFlowRuntimeStateProjectionEventTypes
                                .SubflowChildCompleted
                            : DynamicFlowRuntimeStateProjectionEventTypes
                                .SubflowChildBlocked,
                        CommandId = commandId,
                        CorrelationId = child.Id,
                        SourceEventKey = commandId,
                        FromState = parentStep.State,
                        ToState = parentStepState,
                        FromRevision = parentStep.Revision,
                        ToRevision = parentStep.Revision + 1,
                        ReasonCode = childSucceeded
                            ? "SUBFLOW_CHILD_COMPLETED"
                            : outcomeCode,
                        AffectedRefs =
                        [
                            $"assignment:{parentStep.AssignmentId}",
                            $"step:{parentStep.Id}",
                            $"instance:{child.Id}"
                        ],
                        ActorUserId = actorUserId,
                        VisibleUnitIds =
                        [
                            parentStep.TargetUnitId,
                            parent.IssuerUnitId
                        ],
                        Payload = stepPayload,
                        PayloadHash = Hash(stepPayload.ToJson()),
                        OccurredAtUtc = now
                    },
                    cancellationToken: transactionCt);

                if (instanceEventId is not null)
                {
                    var instancePayload = new BsonDocument
                    {
                        { "sourceEventKey", commandId },
                        { "sourceCommandId", commandId },
                        {
                            "fromState",
                            DynamicFlowInstanceStates.Active
                        },
                        { "toState", parentState },
                        { "reasonCode", outcomeCode },
                        { "childInstanceId", child.Id },
                        {
                            "projectionFingerprint",
                            projectionFingerprint
                        }
                    };
                    await _ctx.DynamicFlowRuntimeEvents
                        .InsertOneAsync(
                            session,
                            new DynamicFlowRuntimeEvent
                            {
                                Id = instanceEventId,
                                FlowInstanceId = parent.Id,
                                Sequence =
                                    parent.NextEventSequence + 1,
                                EventType = parentState switch
                                {
                                    DynamicFlowInstanceStates.Completed =>
                                        DynamicFlowRuntimeStateProjectionEventTypes
                                            .InstanceCompleted,
                                    DynamicFlowInstanceStates.Failed =>
                                        DynamicFlowRuntimeStateProjectionEventTypes
                                            .InstanceFailed,
                                    DynamicFlowInstanceStates.Terminated =>
                                        DynamicFlowRuntimeStateProjectionEventTypes
                                            .InstanceTerminated,
                                    _ => string.Empty
                                },
                                CommandId = commandId,
                                CorrelationId = child.Id,
                                SourceEventKey = commandId,
                                FromState =
                                    DynamicFlowInstanceStates.Active,
                                ToState = parentState,
                                FromRevision = parent.Revision,
                                ToRevision = parent.Revision + 1,
                                ReasonCode = outcomeCode,
                                AffectedRefs =
                                [
                                    $"instance:{parent.Id}",
                                    $"instance:{child.Id}"
                                ],
                                ActorUserId = actorUserId,
                                VisibleUnitIds =
                                [
                                    parentStep.TargetUnitId,
                                    parent.IssuerUnitId
                                ],
                                Payload = instancePayload,
                                PayloadHash =
                                    Hash(instancePayload.ToJson()),
                                OccurredAtUtc = now
                            },
                            cancellationToken: transactionCt);
                }
                if (parentState is
                    DynamicFlowInstanceStates.Completed or
                    DynamicFlowInstanceStates.Failed or
                    DynamicFlowInstanceStates.Terminated)
                {
                    terminalParentInstanceId = parent.Id;
                }
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    parent.WorkId,
                    transactionCt);
            },
            ct);

        if (terminalParentInstanceId is not null)
        {
            await ProjectSubflowParentTerminalAsync(
                terminalParentInstanceId,
                actorUserId,
                ct);
        }
    }

    private static bool IsDuplicateKeyRemainder(Exception error)
    {
        if (error is MongoCommandException command && command.Code == 11000)
            return true;
        if (error is MongoBulkWriteException<DynamicFlowRuntimeEvent> bulk &&
            bulk.WriteErrors.Any(item =>
                item.Category == ServerErrorCategory.DuplicateKey ||
                item.Code == 11000))
        {
            return true;
        }
        return error.InnerException is not null && IsDuplicateKey(error.InnerException);
    }

    private static bool IsRetryableRace(Exception error)
    {
        if (IsDuplicateKey(error))
            return true;
        if (error is InvalidOperationException invalid &&
            invalid.Message == "DYNAMIC_FLOW_REVISION_CONFLICT")
        {
            return true;
        }
        if (error is MongoException mongo &&
            (mongo.HasErrorLabel("TransientTransactionError") ||
             mongo.HasErrorLabel("UnknownTransactionCommitResult")))
        {
            return true;
        }
        if (error is MongoCommandException command && command.Code is 112 or 251)
            return true;
        if (error is MongoWriteException write && write.WriteError?.Code == 112)
            return true;
        return error.InnerException is not null && IsRetryableRace(error.InnerException);
    }

    private static string ResolveReportTargetState(
        int expectedPeriodCount,
        IReadOnlyCollection<ReportProjectionSource> authoritativeReports)
    {
        if (expectedPeriodCount <= 0 || authoritativeReports.Count == 0)
            return DynamicFlowStepStates.Assigned;
        if (authoritativeReports.Any(report => report.IsReturnedDraft))
            return DynamicFlowStepStates.Returned;
        if (authoritativeReports.Count < expectedPeriodCount ||
            authoritativeReports.Any(report =>
                report.Status == WorkAssignmentReportStatus.Draft))
        {
            return DynamicFlowStepStates.InProgress;
        }
        if (authoritativeReports.All(report =>
                report.Status == WorkAssignmentReportStatus.Approved))
        {
            return DynamicFlowStepStates.Approved;
        }
        if (authoritativeReports.All(report =>
                report.Status is WorkAssignmentReportStatus.Submitted
                    or WorkAssignmentReportStatus.Approved))
        {
            return DynamicFlowStepStates.Submitted;
        }
        return DynamicFlowStepStates.InProgress;
    }

    private sealed record ReportProjectionSource(
        string ReportId,
        string PeriodId,
        int LifecycleRevision,
        WorkAssignmentReportStatus Status,
        bool IsActive,
        bool IsReturnedDraft,
        string? EntryKey,
        string? CommandId,
        DateTime EntryCreatedAtUtc);

    private sealed record ReportProjection(
        string TargetState,
        string Fingerprint,
        string? CanonicalReportId,
        string? LifecycleEntryKey,
        string? LifecycleCommandId,
        int? LifecycleRevision,
        string? LifecycleStatus,
        bool? LifecycleIsActive);
}

public static class DynamicFlowRuntimeStateProjectionPolicy
{
    public static string ResolveReportTargetState(
        int expectedPeriodCount,
        IReadOnlyCollection<WorkAssignmentReport> authoritativeReports)
    {
        ArgumentNullException.ThrowIfNull(authoritativeReports);
        if (expectedPeriodCount <= 0 || authoritativeReports.Count == 0)
            return DynamicFlowStepStates.Assigned;
        if (authoritativeReports.Any(report =>
                report.Status == WorkAssignmentReportStatus.Draft &&
                report.ReturnedAtUtc.HasValue))
        {
            return DynamicFlowStepStates.Returned;
        }
        if (authoritativeReports.Count < expectedPeriodCount ||
            authoritativeReports.Any(report =>
                report.Status == WorkAssignmentReportStatus.Draft))
        {
            return DynamicFlowStepStates.InProgress;
        }
        if (authoritativeReports.All(report =>
                report.Status == WorkAssignmentReportStatus.Approved))
        {
            return DynamicFlowStepStates.Approved;
        }
        if (authoritativeReports.All(report =>
                report.Status is WorkAssignmentReportStatus.Submitted
                    or WorkAssignmentReportStatus.Approved))
        {
            return DynamicFlowStepStates.Submitted;
        }
        return DynamicFlowStepStates.InProgress;
    }

    public static IReadOnlyList<string> BuildStepPath(string fromState, string toState)
    {
        if (!DynamicFlowStepStates.All.Contains(fromState))
            throw new InvalidOperationException($"DYNAMIC_FLOW_STEP_STATE_INVALID:{fromState}");
        if (!DynamicFlowStepStates.All.Contains(toState))
            throw new InvalidOperationException($"DYNAMIC_FLOW_STEP_STATE_INVALID:{toState}");
        if (fromState == toState)
            return Array.Empty<string>();
        if (fromState is DynamicFlowStepStates.Pending
            or DynamicFlowStepStates.Materializing
            or DynamicFlowStepStates.Partial
            or DynamicFlowStepStates.Retrying
            or DynamicFlowStepStates.Reconciled
            or DynamicFlowStepStates.Failed
            or DynamicFlowStepStates.Terminated)
        {
            throw new InvalidOperationException(
                $"DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_SOURCE_INVALID:{fromState}");
        }
        if (fromState == DynamicFlowStepStates.Completed)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_COMPLETED_TERMINAL");
        }

        var path = new List<string>();
        var current = fromState;
        for (var guard = 0; guard < 8 && current != toState; guard++)
        {
            var next = current switch
            {
                DynamicFlowStepStates.Assigned => DynamicFlowStepStates.InProgress,
                DynamicFlowStepStates.Returned => DynamicFlowStepStates.InProgress,
                DynamicFlowStepStates.InProgress when toState == DynamicFlowStepStates.InProgress =>
                    DynamicFlowStepStates.InProgress,
                DynamicFlowStepStates.InProgress => DynamicFlowStepStates.Submitted,
                DynamicFlowStepStates.Submitted when toState is
                    DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed =>
                    DynamicFlowStepStates.Approved,
                DynamicFlowStepStates.Submitted when toState == DynamicFlowStepStates.Submitted =>
                    DynamicFlowStepStates.Submitted,
                DynamicFlowStepStates.Submitted => DynamicFlowStepStates.Returned,
                DynamicFlowStepStates.Approved when toState == DynamicFlowStepStates.Completed =>
                    DynamicFlowStepStates.Completed,
                DynamicFlowStepStates.Approved when toState == DynamicFlowStepStates.Approved =>
                    DynamicFlowStepStates.Approved,
                DynamicFlowStepStates.Approved => DynamicFlowStepStates.Returned,
                _ => throw new InvalidOperationException(
                    $"DYNAMIC_FLOW_STEP_TRANSITION_INVALID:{current}:{toState}")
            };
            if (next == current)
                break;
            DynamicFlowRuntimeStateContract.RequireStepTransition(current, next);
            path.Add(next);
            current = next;
        }

        if (current != toState)
        {
            throw new InvalidOperationException(
                $"DYNAMIC_FLOW_STEP_TRANSITION_INVALID:{fromState}:{toState}");
        }
        return path;
    }

    public static string ResolveForwardGuardReason(string parentStepState)
        => parentStepState is DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed
            ? "DYNAMIC_FLOW_COMMAND_BLOCKED_UNTIL_P6"
            : "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED";
}
