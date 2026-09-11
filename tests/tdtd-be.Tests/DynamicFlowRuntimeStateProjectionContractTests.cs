using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowRuntimeStateProjectionContractTests
{
    private static readonly string[] LifecycleStates =
    [
        DynamicFlowStepStates.Assigned,
        DynamicFlowStepStates.InProgress,
        DynamicFlowStepStates.Submitted,
        DynamicFlowStepStates.Returned,
        DynamicFlowStepStates.Approved,
        DynamicFlowStepStates.Completed
    ];

    public static void Run()
    {
        DirectTransitionMatrixIsFrozen();
        InstanceTransitionMatrixIsFrozen();
        InvalidDirectTransitionsExposeExactStableReasons();
        LifecyclePathsUseOnlyLegalTransitions();
        AggregateReportStateUsesEveryExpectedReport();
        CompletedStateIsTerminalForLateLifecycleProjection();
        ForwardGuardReasonsAreStable();
        FaultMatrixHasExactlyTenTestingOnlyBoundaries();
        ReportProjectionIdentityAndHooksAreDeterministic();
        CompletionAndLifecycleUseOneDurableFence();
        ProgressRecomputeUsesObservedRevisionCas();
        ForwardGuardIsServerDerivedAndZeroWrite();
    }

    private static void InstanceTransitionMatrixIsFrozen()
    {
        var expected = new HashSet<(string From, string To)>
        {
            (DynamicFlowInstanceStates.Pending, DynamicFlowInstanceStates.Materializing),
            (DynamicFlowInstanceStates.Materializing, DynamicFlowInstanceStates.Active),
            (DynamicFlowInstanceStates.Materializing, DynamicFlowInstanceStates.Partial),
            (DynamicFlowInstanceStates.Materializing, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Completed),
            (DynamicFlowInstanceStates.Completed, DynamicFlowInstanceStates.Active),
            (DynamicFlowInstanceStates.Completed, DynamicFlowInstanceStates.Finalized),
            (DynamicFlowInstanceStates.Completed, DynamicFlowInstanceStates.Terminated),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Finalized),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Partial),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Active, DynamicFlowInstanceStates.Terminated),
            (DynamicFlowInstanceStates.Partial, DynamicFlowInstanceStates.Retrying),
            (DynamicFlowInstanceStates.Partial, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Retrying, DynamicFlowInstanceStates.Reconciled),
            (DynamicFlowInstanceStates.Retrying, DynamicFlowInstanceStates.Failed),
            (DynamicFlowInstanceStates.Reconciled, DynamicFlowInstanceStates.Active)
        };

        foreach (var from in DynamicFlowInstanceStates.All)
        {
            foreach (var to in DynamicFlowInstanceStates.All)
            {
                Require(
                    DynamicFlowRuntimeStateContract.CanTransitionInstance(from, to) ==
                    expected.Contains((from, to)),
                    $"direct instance transition matrix drift: {from} -> {to}");
            }
        }
    }

    private static void DirectTransitionMatrixIsFrozen()
    {
        var expected = new HashSet<(string From, string To)>
        {
            (DynamicFlowStepStates.Pending, DynamicFlowStepStates.Materializing),
            (DynamicFlowStepStates.Materializing, DynamicFlowStepStates.Assigned),
            (DynamicFlowStepStates.Materializing, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Materializing, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.Assigned, DynamicFlowStepStates.InProgress),
            (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Returned),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved),
            (DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Returned),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Completed),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.WaitingChild),
            (DynamicFlowStepStates.WaitingChild, DynamicFlowStepStates.Completed),
            (DynamicFlowStepStates.WaitingChild, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.WaitingChild, DynamicFlowStepStates.Terminated),
            (DynamicFlowStepStates.Assigned, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Returned, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.CancelledByGateway),
            (DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Returned, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Approved, DynamicFlowStepStates.Partial),
            (DynamicFlowStepStates.Partial, DynamicFlowStepStates.Retrying),
            (DynamicFlowStepStates.Partial, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.Retrying, DynamicFlowStepStates.Reconciled),
            (DynamicFlowStepStates.Retrying, DynamicFlowStepStates.Failed),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Assigned),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.InProgress),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Submitted),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Returned),
            (DynamicFlowStepStates.Reconciled, DynamicFlowStepStates.Approved)
        };

        foreach (var from in DynamicFlowStepStates.All)
        {
            foreach (var to in DynamicFlowStepStates.All)
            {
                Require(
                    DynamicFlowRuntimeStateContract.CanTransitionStep(from, to) ==
                    expected.Contains((from, to)),
                    $"direct step transition matrix drift: {from} -> {to}");
            }
        }
    }

    private static void InvalidDirectTransitionsExposeExactStableReasons()
    {
        foreach (var from in DynamicFlowInstanceStates.All)
        {
            foreach (var to in DynamicFlowInstanceStates.All)
            {
                if (DynamicFlowRuntimeStateContract.CanTransitionInstance(
                        from,
                        to))
                {
                    continue;
                }
                AssertThrowsExact(
                    () =>
                        DynamicFlowRuntimeStateContract
                            .RequireInstanceTransition(from, to),
                    $"DYNAMIC_FLOW_INSTANCE_TRANSITION_INVALID:{from}:{to}");
            }
        }

        foreach (var from in DynamicFlowStepStates.All)
        {
            foreach (var to in DynamicFlowStepStates.All)
            {
                if (DynamicFlowRuntimeStateContract.CanTransitionStep(
                        from,
                        to))
                {
                    continue;
                }
                AssertThrowsExact(
                    () =>
                        DynamicFlowRuntimeStateContract
                            .RequireStepTransition(from, to),
                    $"DYNAMIC_FLOW_STEP_TRANSITION_INVALID:{from}:{to}");
            }
        }
    }

    private static void LifecyclePathsUseOnlyLegalTransitions()
    {
        var expectedPaths = new Dictionary<(string From, string To), string[]>
        {
            [(DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Assigned)] = [],
            [(DynamicFlowStepStates.Assigned, DynamicFlowStepStates.InProgress)] =
                [DynamicFlowStepStates.InProgress],
            [(DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Submitted)] =
                [DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted],
            [(DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Returned)] =
                [DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Returned],
            [(DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Approved)] =
                [DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved],
            [(DynamicFlowStepStates.Assigned, DynamicFlowStepStates.Completed)] =
                [
                    DynamicFlowStepStates.InProgress,
                    DynamicFlowStepStates.Submitted,
                    DynamicFlowStepStates.Approved,
                    DynamicFlowStepStates.Completed
                ],
            [(DynamicFlowStepStates.InProgress, DynamicFlowStepStates.InProgress)] = [],
            [(DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted)] =
                [DynamicFlowStepStates.Submitted],
            [(DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Returned)] =
                [DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Returned],
            [(DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Approved)] =
                [DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved],
            [(DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Completed)] =
                [DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved, DynamicFlowStepStates.Completed],
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.InProgress)] =
                [DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress],
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Submitted)] = [],
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Returned)] =
                [DynamicFlowStepStates.Returned],
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved)] =
                [DynamicFlowStepStates.Approved],
            [(DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Completed)] =
                [DynamicFlowStepStates.Approved, DynamicFlowStepStates.Completed],
            [(DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress)] =
                [DynamicFlowStepStates.InProgress],
            [(DynamicFlowStepStates.Returned, DynamicFlowStepStates.Submitted)] =
                [DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted],
            [(DynamicFlowStepStates.Returned, DynamicFlowStepStates.Returned)] = [],
            [(DynamicFlowStepStates.Returned, DynamicFlowStepStates.Approved)] =
                [DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted, DynamicFlowStepStates.Approved],
            [(DynamicFlowStepStates.Returned, DynamicFlowStepStates.Completed)] =
                [
                    DynamicFlowStepStates.InProgress,
                    DynamicFlowStepStates.Submitted,
                    DynamicFlowStepStates.Approved,
                    DynamicFlowStepStates.Completed
                ],
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.InProgress)] =
                [DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress],
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.Submitted)] =
                [DynamicFlowStepStates.Returned, DynamicFlowStepStates.InProgress, DynamicFlowStepStates.Submitted],
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.Returned)] =
                [DynamicFlowStepStates.Returned],
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.Approved)] = [],
            [(DynamicFlowStepStates.Approved, DynamicFlowStepStates.Completed)] =
                [DynamicFlowStepStates.Completed],
            [(DynamicFlowStepStates.Completed, DynamicFlowStepStates.Completed)] = []
        };

        foreach (var pair in expectedPaths)
        {
            var path = DynamicFlowRuntimeStateProjectionPolicy.BuildStepPath(
                pair.Key.From,
                pair.Key.To);
            Require(
                path.SequenceEqual(pair.Value, StringComparer.Ordinal),
                $"lifecycle path drift: {pair.Key.From} -> {pair.Key.To}");

            var current = pair.Key.From;
            foreach (var next in path)
            {
                Require(
                    DynamicFlowRuntimeStateContract.CanTransitionStep(current, next),
                    $"lifecycle path contains illegal edge: {current} -> {next}");
                current = next;
            }
            Require(current == pair.Key.To, $"lifecycle path did not reach target: {pair.Key.From} -> {pair.Key.To}");
        }

        foreach (var from in LifecycleStates)
        {
            foreach (var to in LifecycleStates)
            {
                if (expectedPaths.ContainsKey((from, to)))
                    continue;
                AssertThrows(
                    () => DynamicFlowRuntimeStateProjectionPolicy.BuildStepPath(from, to),
                    from == DynamicFlowStepStates.Completed
                        ? "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_COMPLETED_TERMINAL"
                        : "DYNAMIC_FLOW_STEP_TRANSITION_INVALID");
            }
        }
    }

    private static void AggregateReportStateUsesEveryExpectedReport()
    {
        Require(
            Resolve(0) == DynamicFlowStepStates.Assigned &&
            Resolve(2) == DynamicFlowStepStates.Assigned,
            "no-report aggregate must remain ASSIGNED");
        Require(
            Resolve(2, Draft()) == DynamicFlowStepStates.InProgress,
            "a missing participant report must prevent aggregate submit");
        Require(
            Resolve(2, Submitted()) == DynamicFlowStepStates.InProgress,
            "one submitted participant must not advance a two-report step");
        Require(
            Resolve(2, Submitted(), Draft()) == DynamicFlowStepStates.InProgress,
            "a draft sibling must prevent aggregate submit");
        Require(
            Resolve(2, Submitted(), Submitted()) == DynamicFlowStepStates.Submitted,
            "all expected reports submitted must project SUBMITTED");
        Require(
            Resolve(2, Submitted(), Approved()) == DynamicFlowStepStates.Submitted,
            "mixed submitted/approved reports must remain SUBMITTED");
        Require(
            Resolve(2, Approved(), Approved()) == DynamicFlowStepStates.Approved,
            "all expected reports approved must project APPROVED");
        Require(
            Resolve(2, ReturnedDraft(), Approved()) == DynamicFlowStepStates.Returned,
            "a returned report must dominate approved siblings");
        Require(
            Resolve(2, Inactive(Approved()), Inactive(Approved())) == DynamicFlowStepStates.Approved,
            "deactivation must not erase authoritative workflow state");
    }

    private static void CompletedStateIsTerminalForLateLifecycleProjection()
    {
        Require(
            DynamicFlowRuntimeStateProjectionPolicy.BuildStepPath(
                DynamicFlowStepStates.Completed,
                DynamicFlowStepStates.Completed).Count == 0,
            "exact late replay on COMPLETED must be a no-op");
        foreach (var target in LifecycleStates.Where(x => x != DynamicFlowStepStates.Completed))
        {
            AssertThrows(
                () => DynamicFlowRuntimeStateProjectionPolicy.BuildStepPath(
                    DynamicFlowStepStates.Completed,
                    target),
                "DYNAMIC_FLOW_RUNTIME_STATE_PROJECTION_COMPLETED_TERMINAL");
        }

        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeStateProjection.cs");
        Require(
            source.Contains(
                "step.State == DynamicFlowStepStates.Completed &&",
                StringComparison.Ordinal) &&
            source.Contains(
                "DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle or",
                StringComparison.Ordinal) &&
            source.Contains(
                "DynamicFlowRuntimeStateProjectionOperations.StateReconcile",
                StringComparison.Ordinal),
            "late report lifecycle guard for a completed step is missing");
        Require(
            source.Contains("effectiveTarget = DynamicFlowStepStates.Completed;", StringComparison.Ordinal),
            "late report lifecycle projection must not reopen a completed step");
    }

    private static void ForwardGuardReasonsAreStable()
    {
        foreach (var state in DynamicFlowStepStates.All)
        {
            var expected = state is DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed
                ? "DYNAMIC_FLOW_COMMAND_BLOCKED_UNTIL_P6"
                : "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED";
            Require(
                DynamicFlowRuntimeStateProjectionPolicy.ResolveForwardGuardReason(state) == expected,
                $"forward guard reason drift for {state}");
        }
    }

    private static void FaultMatrixHasExactlyTenTestingOnlyBoundaries()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeTransaction,
            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeReceiptWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterReceiptWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeStepWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterStepWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterJoinContributionWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterJoinGatewayWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeInstanceWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterInstanceWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.BeforeEventWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterEventWrite,
            DynamicFlowRuntimeStateProjectionFaultPoints.AfterTransactionCommit
        };
        Require(expected.Count == 12, "test expectation must enumerate exactly twelve fault boundaries");
        Require(
            DynamicFlowRuntimeStateProjectionFaultPoints.All.SetEquals(expected),
            "state projection fault boundary set drift");

        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeStateProjection.cs");
        var injector = Slice(
            source,
            "public sealed class DynamicFlowRuntimeStateProjectionFaultInjector",
            "public sealed record DynamicFlowRuntimeStateProjectionResult");
        Require(
            injector.Contains("environment.IsEnvironment(\"Testing\")", StringComparison.Ordinal),
            "fault injection must be Testing-only");
        Require(
            injector.Contains("_consumed.TryAdd(faultPoint, 0)", StringComparison.Ordinal),
            "each configured fault boundary must fire at most once per process");
    }

    private static void ReportProjectionIdentityAndHooksAreDeterministic()
    {
        var projection = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeStateProjection.cs");
        Require(
            projection.Contains(
                "var sourceEventKey = Hash($\"{sourceReport.Id}\\n{lifecycleEntryKey}\");",
                StringComparison.Ordinal),
            "report source-event namespace must bind report id and P3 entry key");
        Require(
            projection.Contains(
                "$\"{DynamicFlowCommandScopeKinds.Instance}\\n{flowInstanceId}\\n{commandType}\\n{sourceEventKey}\"",
                StringComparison.Ordinal),
            "projection receipt namespace must bind instance, command type, and source-event key");
        Require(
            projection.Contains(
                "$\"{flowInstanceId}\\nstate-event\\n{receiptId}\\n{index}\"",
                StringComparison.Ordinal),
            "runtime state event ids must derive from the deterministic receipt namespace");
        Require(
            projection.Contains("CommandId = sourceEventKey", StringComparison.Ordinal) &&
            projection.Contains("CorrelationId = sourceCommandId", StringComparison.Ordinal) &&
            projection.Contains("SourceEventKey = sourceEventKey", StringComparison.Ordinal),
            "runtime event source/correlation fields are incomplete");

        var lifecycle = ReadSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs");
        var lifecycleMethod = Slice(
            lifecycle,
            "private async Task ReconcileClaimAsync(",
            "private async Task<LifecycleProjectionClaim?> TryClaimAsync(");
        Require(
            lifecycleMethod.Contains(
                "_dynamicFlowRuntimeStateProjector.ProjectReportLifecycleAsync(",
                StringComparison.Ordinal),
            "P3 durable lifecycle reconciler does not invoke the runtime state projector");

        var assignment = ReadSource("Services/WorkAssignments/WorkAssignmentService.cs");
        var completion = Slice(
            assignment,
            "private async Task ConvergeCompletedAssignmentAsync(",
            "private async Task EnsureAssignmentCompletionReadyAsync(");
        Require(
            completion.Contains(
                "_dynamicFlowRuntimeStateProjector.ProjectAssignmentCompletionAsync(",
                StringComparison.Ordinal),
            "assignment completion convergence does not invoke the runtime state projector");
    }

    private static void CompletionAndLifecycleUseOneDurableFence()
    {
        var assignment = ReadSource(
            "Services/WorkAssignments/WorkAssignmentService.cs");
        var completion = Slice(
            assignment,
            "public async Task<WorkAssignmentResponse?> CompleteAsync(",
            "private async Task ConvergeCompletedAssignmentAsync(");
        Require(
            completion.Contains(
                "_lifecycleSeriesLock.AcquireAsync(",
                StringComparison.Ordinal) &&
            completion.Contains(
                "WorkReportLifecycleSeriesOperations.AssignmentComplete",
                StringComparison.Ordinal) &&
            completion.Contains(
                "completionLease.RenewAsync(ct)",
                StringComparison.Ordinal) &&
            completion.Contains(
                "completionLease.LeaseId",
                StringComparison.Ordinal) &&
            completion.Contains(
                "Filter.Eq(x => x.IsActive, true)",
                StringComparison.Ordinal) &&
            completion.Contains(
                "reason = \"WORK_ASSIGNMENT_INACTIVE\"",
                StringComparison.Ordinal),
            "Dynamic Flow completion must require an active assignment and CAS against the lifecycle lease");
        Require(
            assignment.Contains(
                "DYNAMIC_FLOW_ASSIGNMENT_MUTATION_BLOCKED_UNTIL_P6",
                StringComparison.Ordinal) &&
            assignment.Contains(
                "command = \"DEACTIVATE_ASSIGNMENT\"",
                StringComparison.Ordinal) &&
            assignment.Contains(
                "command = \"ACTIVATE_ASSIGNMENT\"",
                StringComparison.Ordinal) &&
            assignment.Contains(
                "command = \"CREATE_CHILD_ASSIGNMENT\"",
                StringComparison.Ordinal) &&
            assignment.Contains(
                ".Find(x => x.WorkId == workId && !x.IsDeleted)",
                StringComparison.Ordinal) &&
            assignment.Contains(
                "? \"CREATE_ROOT_ASSIGNMENT\"",
                StringComparison.Ordinal) &&
            assignment.Contains(
                "command = \"UPDATE_DATA_SOURCE_RULES\"",
                StringComparison.Ordinal) &&
            assignment.Contains(
                "command = \"UPDATE_AUTO_APPROVE_CONDITION\"",
                StringComparison.Ordinal),
            "flow-owned assignment topology, config and activation mutations must remain zero-write phase guards");

        var lifecycleFence = ReadSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleSeriesLockService.cs");
        Require(
            lifecycleFence.Contains(
                "WorkReportLifecycleSeriesOperations.AssignmentComplete",
                StringComparison.Ordinal) &&
            lifecycleFence.Contains(
                "fb.Eq(x => x.CompletedAtUtc, null)",
                StringComparison.Ordinal) &&
            lifecycleFence.Contains(
                "\"DYNAMIC_FLOW_ASSIGNMENT_COMPLETED\"",
                StringComparison.Ordinal) &&
            lifecycleFence.Contains(
                "public const string AggregateRefresh = \"P5_AGGREGATE_REFRESH\"",
                StringComparison.Ordinal),
            "report lifecycle lock must reject new mutations after Dynamic Flow completion");

        var projection = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeStateProjection.cs");
        var readiness = Slice(
            projection,
            "public async Task<bool> IsAssignmentCompletionProjectionReadyAsync(",
            "public async Task<DynamicFlowRuntimeStateReconcileResult> ReconcileInstanceAsync(");
        Require(
            readiness.Contains(
                "WorkReportLifecycleProjectionOutboxStates.Completed",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "DynamicFlowRuntimeStateProjectionOperations.ReportLifecycle",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "report.IsCurrent &&",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "report.IsActive &&",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "WorkAssignmentReportStatus.Approved",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "report.AggregateSnapshotDirty",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "report.PayloadMutationCommandId",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "instance.State != DynamicFlowInstanceStates.Active",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "materializationItems.Count != allSteps.Count",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "MaterializeSequentialAssignment",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "MaterializeForkBranchAssignment",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "MaterializeReviewAttemptAssignment",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "\"FLOW-T05\" or\n                \"FLOW-T06\" or\n                \"FLOW-T07\" or\n                \"FLOW-T08\" or\n                \"FLOW-T12\"))",
                StringComparison.Ordinal) &&
            readiness.Contains(
                "\"FLOW-T05\" or\n            \"FLOW-T06\" or\n            \"FLOW-T07\" or\n            \"FLOW-T08\"\n            ? steps[0].State is",
                StringComparison.Ordinal) &&
            readiness.Contains(
                ": steps[0].State == DynamicFlowStepStates.Approved",
                StringComparison.Ordinal),
            "completion readiness must fail closed until lifecycle, aggregate and materialization state are stable");
        Require(
            projection.Contains(
                "ExecutionEpoch = step.ExecutionEpoch",
                StringComparison.Ordinal) &&
            projection.Contains(
                "BranchId = step.BranchId",
                StringComparison.Ordinal) &&
            projection.Contains(
                "AttemptNo = step.AttemptNo",
                StringComparison.Ordinal) &&
            projection.Contains(
                "{ \"definitionRevision\", step.DefinitionRevision }",
                StringComparison.Ordinal) &&
            projection.Contains(
                "{ \"terminalStepInstanceId\", step.Id }",
                StringComparison.Ordinal),
            "state and terminal-completion events must carry exact epoch/step/branch/attempt identity");
        Require(
            projection.Contains(
                "var completionProjectionDebt = assignments",
                StringComparison.Ordinal) &&
            projection.Contains(
                ".Concat(completionProjectionDebt.Select(item => item.Id))",
                StringComparison.Ordinal),
            "state reconcile must detect and repair missing T01/T02 assignment completion receipts");
        Require(
            projection.Contains(
                "if (step.State == DynamicFlowStepStates.Completed)",
                StringComparison.Ordinal) &&
            projection.Contains(
                "target = DynamicFlowStepStates.Completed;",
                StringComparison.Ordinal) &&
            projection.Contains(
                "including a forwarded T03 node",
                StringComparison.Ordinal),
            "state reconcile preview must never reopen a completed sequential node");

        var reportResolver = Slice(
            projection,
            "private async Task<ReportProjection> ResolveReportProjectionAsync(",
            "private static (string From, string To)[] BuildTransitions(");
        var activeAuthorityIndex = reportResolver.IndexOf(
            ".OrderByDescending(report => report.IsCurrent && report.IsActive)",
            StringComparison.Ordinal);
        var triggerOverrideIndex = reportResolver.IndexOf(
            ".ThenByDescending(report =>",
            activeAuthorityIndex + 1,
            StringComparison.Ordinal);
        Require(
            activeAuthorityIndex >= 0 &&
            triggerOverrideIndex > activeAuthorityIndex &&
            reportResolver.Contains(
                ".ThenByDescending(report => report.IsCurrent)",
                StringComparison.Ordinal) &&
            reportResolver.Contains(
                "string.Equals(report.Id, overrideReportId, StringComparison.Ordinal)",
                StringComparison.Ordinal),
            "active/current report authority must outrank a delayed lifecycle trigger");
        var directProjection = ReadSource(
            "Services/StatisticsRun/StatRunDirectProjectionService.cs");
        var directRuntimeAdmission = Slice(
            directProjection,
            "private async Task<DirectRuntimePin?> ResolveEffectiveRuntimeAsync(",
            "private void LogRuntimeAdmissionRejected(");
        Require(
            reportResolver.Contains(
                "var canonicalReport = projectionSources.Length == 1",
                StringComparison.Ordinal) &&
            directRuntimeAdmission.Contains(
                ": step.ReportId is not null &&",
                StringComparison.Ordinal) &&
            directRuntimeAdmission.Contains(
                "!string.Equals(step.ReportId, report.Id, StringComparison.Ordinal)",
                StringComparison.Ordinal),
            "multi-period runtime owners must accept the canonical null report id while rejecting a non-null mismatched report pin");
        Require(
            directProjection.Contains(
                "tenantApprovalActorUserId = item.Tenant.ApprovalActorUserId",
                StringComparison.Ordinal),
            "Direct membership canonical JSON must keep outbox and tenant approval actors under distinct property names");
        Require(
            projection.Contains(
                "? sourceReportId",
                StringComparison.Ordinal) &&
            projection.Contains(
                "var eventReportId = commandType ==",
                StringComparison.Ordinal),
            "lifecycle events must retain ownership by their source report/entry");

        var materializer = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        var reconcile = Slice(
            materializer,
            "public async Task<DynamicFlowRuntimeReconcileResult> ReconcileAsync(",
            "private async Task RepairUnprojectedReportLifecycleAsync(");
        var lifecycleDrainIndex = reconcile.IndexOf(
            "lifecycleState = await DrainReportLifecycleDebtAsync(",
            StringComparison.Ordinal);
        var exactLedgerGateIndex = reconcile.IndexOf(
            "if (preview.ExactMaterializationLedgerConverged)",
            StringComparison.Ordinal);
        Require(
            lifecycleDrainIndex >= 0 &&
            exactLedgerGateIndex > lifecycleDrainIndex &&
            materializer.Contains(
                "private async Task<DynamicFlowRuntimeStateReconcileResult> DrainReportLifecycleDebtAsync(",
                StringComparison.Ordinal) &&
            reconcile.Contains(
                "return MergeStateReconcile(preview, lifecycleState)",
                StringComparison.Ordinal),
            "apply reconcile must drain P3/P5 debt and defer while a P3 claim remains before exact-ledger gating");
        var reportService = ReadSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var aggregateRefresh = Slice(
            reportService,
            "private async Task RefreshAggregateDependentAfterSourceChangeAsync(",
            "private async Task ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync(");
        Require(
            aggregateRefresh.Contains(
                "AcquireAggregateRefreshLeaseIfDynamicFlowAsync(",
                StringComparison.Ordinal) &&
            aggregateRefresh.Contains(
                "await aggregateRefreshLease.RenewAsync(ct)",
                StringComparison.Ordinal) &&
            aggregateRefresh.Contains(
                "existingLifecycleSeriesLease ?? ownedLifecycleSeriesLease!",
                StringComparison.Ordinal) &&
            aggregateRefresh.Contains(
                "aggregateRefreshFence.BlockedByCompletedFlow",
                StringComparison.Ordinal) &&
            aggregateRefresh.Contains(
                ".Set(x => x.AggregateSnapshotDirty, false)",
                StringComparison.Ordinal) &&
            reportService.Contains(
                "var preserveAggregateDirty =",
                StringComparison.Ordinal),
            "approved aggregate payload refresh and lifecycle invalidation must share the completion fence");
        Require(
            materializer.Contains(
                "!queueAssignment.CompletedAtUtc.HasValue",
                StringComparison.Ordinal) &&
            materializer.Contains(
                "await _queue.DisableByAssignmentAsync(",
                StringComparison.Ordinal),
            "materialization reconcile must preserve completed-assignment queue shutdown");
        Require(
            materializer.Contains(
                "reportByIdForPeriodSource",
                StringComparison.Ordinal) &&
            materializer.Contains(
                "inactiveLifecycleSource.WorkReportPeriodId ==",
                StringComparison.Ordinal) &&
            materializer.Contains(
                "activeLifecycleSource.LifecycleRevision > 0",
                StringComparison.Ordinal) &&
            materializer.Contains(
                "priorLifecycleSource.WorkReportPeriodId ==",
                StringComparison.Ordinal),
            "exact period reconciliation must preserve projected and revision-zero lifecycle provenance");
    }

    private static void ProgressRecomputeUsesObservedRevisionCas()
    {
        var source = ReadSource(
            "Services/WorkAssignments/Progress/WorkAssignmentProgressService.cs");
        var single = Slice(
            source,
            "public async Task<ProgressRecomputeResult> RecomputeSingleAsync(",
            "public async Task<List<ProgressRecomputeResult>> RecomputeDirectChildrenAsync(");
        Require(
            single.Contains(
                "var expectedProgressStatusUpdatedAtUtc = current.ProgressStatusUpdatedAtUtc;",
                StringComparison.Ordinal) &&
            single.Contains(
                "BuildObservedProgressCasFilter(",
                StringComparison.Ordinal) &&
            single.Contains(
                "attempt >= MaxProgressCasAttempts",
                StringComparison.Ordinal) &&
            single.Contains(
                "if (updateResult.MatchedCount == 0)",
                StringComparison.Ordinal) &&
            single.Contains(
                "WORK_ASSIGNMENT_PROGRESS_CAS_RETRY_EXHAUSTED",
                StringComparison.Ordinal),
            "single progress recompute must reject stale lifecycle facts with a bounded retry from current assignment state");

        var children = Slice(
            source,
            "public async Task<List<ProgressRecomputeResult>> RecomputeDirectChildrenAsync(",
            "public async Task<List<ProgressRecomputeResult>> RecomputeParentChainAsync(");
        Require(
            children.Contains(
                "var expectedProgressStatusUpdatedAtUtc = child.ProgressStatusUpdatedAtUtc;",
                StringComparison.Ordinal) &&
            children.Contains(
                "BuildObservedProgressCasFilter(",
                StringComparison.Ordinal) &&
            children.Contains(
                "if (updateResult.MatchedCount == 0)",
                StringComparison.Ordinal) &&
            children.Contains(
                "results.Add(await RecomputeSingleAsync(refreshed, ct));",
                StringComparison.Ordinal),
            "direct-child progress recompute must reject stale lifecycle facts and retry from current assignment state");

        Require(
            source.Contains(
                "BuildDynamicFlowMaterializationRevisionFilter(long expectedRevision)",
                StringComparison.Ordinal) &&
            source.Contains(
                "x => x.DynamicFlowMaterializationRevision",
                StringComparison.Ordinal) &&
            source.Contains(
                "BuildReportLifecycleSeriesRevisionFilter(long expectedRevision)",
                StringComparison.Ordinal) &&
            source.Contains(
                "x => x.ReportLifecycleSeriesRevision",
                StringComparison.Ordinal) &&
            source.Contains(
                "return expectedRevision == 0",
                StringComparison.Ordinal) &&
            source.Contains(
                "false))",
                StringComparison.Ordinal),
            "progress revision CAS must treat only a missing legacy zero revision as zero while preserving exact nonzero matches");
    }

    private static void ForwardGuardIsServerDerivedAndZeroWrite()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var forward = Slice(
            source,
            "public async Task<DynamicFlowBranchActionResponse> ForwardBranchAsync(",
            "private async Task<DynamicFlowBranchActionResponse> MutateBranchAsync(");

        Require(
            forward.Contains("_ctx.WorkAssignments", StringComparison.Ordinal) &&
            forward.Contains("_ctx.DynamicFlowStepInstances", StringComparison.Ordinal) &&
            forward.Contains("_ctx.DynamicFlowInstances", StringComparison.Ordinal) &&
            forward.Contains(
                "assignment.FlowEffectiveStatus != DynamicFlowEffectiveStatuses.Effective",
                StringComparison.Ordinal) &&
            forward.Contains("RequireSequentialRuntimePins(", StringComparison.Ordinal) &&
            forward.Contains("RequireSequentialParticipantPins(", StringComparison.Ordinal),
            "forward guard must derive exact parent, topology, and actor state from server-owned records");
        Require(
            forward.Contains(
                "AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                StringComparison.Ordinal) &&
            forward.Contains("canExecute = false", StringComparison.Ordinal),
            "forward guard must return the stable blocked contract");
        var archetypeGate = forward.IndexOf(
            "DynamicFlowSequentialTopologyContract.ArchetypeId",
            StringComparison.Ordinal);
        var transaction = forward.IndexOf(
            "_transactions.ExecuteAsync(",
            StringComparison.Ordinal);
        Require(
            archetypeGate >= 0 && transaction > archetypeGate,
            "T04..T12 must be rejected before the positive T03 transaction");
        var replayLookup = forward.IndexOf(
            "var existing = await _ctx.DynamicFlowRuntimeCommandReceipts",
            StringComparison.Ordinal);
        var revisionGuard = forward.IndexOf(
            "instance.Revision != req.ExpectedInstanceRevision",
            StringComparison.Ordinal);
        Require(
            replayLookup >= 0 && revisionGuard > replayLookup,
            "exact replay must resolve before mutable revision guards");
        foreach (var requiredWriter in new[]
                 {
                     "DynamicFlowRuntimeCommandReceipts.InsertOneAsync",
                     "DynamicFlowStepInstances.InsertOneAsync",
                     "DynamicFlowRuntimeOutbox.InsertOneAsync",
                     "DynamicFlowRuntimeEvents.InsertOneAsync",
                     "DynamicFlowInstances.UpdateOneAsync",
                     "DynamicFlowMaterializationRevision"
                 })
        {
            Require(
                forward.Contains(requiredWriter, StringComparison.Ordinal),
                $"T03 transaction is missing {requiredWriter}");
        }

        var readSource = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeReadService.cs");
        var forwardCapability = Slice(
            readSource,
            "private async Task<DynamicFlowSequentialForwardReadState>",
            "private static DynamicFlowRuntimeStepRow MapStep(");
        Require(
            forwardCapability.Contains(
                "MatchesSequentialParticipantPins(",
                StringComparison.Ordinal) &&
            forwardCapability.Contains(
                "SequentialParticipantSnapshotHash(snapshot)",
                StringComparison.Ordinal) &&
            forwardCapability.Contains(
                "CanActorControlSequentialAssignment(",
                StringComparison.Ordinal) &&
            !forwardCapability.Contains(
                "(isIssuer || isReporter)",
                StringComparison.Ordinal),
            "read-side forward capability must use the same exact actor and participant pins as the writer");
    }

    private static string Resolve(
        int expectedPeriodCount,
        params WorkAssignmentReport[] reports)
        => DynamicFlowRuntimeStateProjectionPolicy.ResolveReportTargetState(
            expectedPeriodCount,
            reports);

    private static WorkAssignmentReport Draft() => new()
    {
        Status = WorkAssignmentReportStatus.Draft,
        IsActive = true
    };

    private static WorkAssignmentReport ReturnedDraft() => new()
    {
        Status = WorkAssignmentReportStatus.Draft,
        ReturnedAtUtc = DateTime.UtcNow,
        IsActive = true
    };

    private static WorkAssignmentReport Submitted() => new()
    {
        Status = WorkAssignmentReportStatus.Submitted,
        IsActive = true
    };

    private static WorkAssignmentReport Approved() => new()
    {
        Status = WorkAssignmentReportStatus.Approved,
        IsActive = true
    };

    private static WorkAssignmentReport Inactive(WorkAssignmentReport report)
    {
        report.IsActive = false;
        return report;
    }

    private static void AssertThrows(Action action, string expectedMessageFragment)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error)
        {
            Require(
                error.Message.Contains(expectedMessageFragment, StringComparison.Ordinal),
                $"expected '{expectedMessageFragment}', got '{error.Message}'");
            return;
        }
        throw new InvalidOperationException(
            $"expected InvalidOperationException containing '{expectedMessageFragment}'");
    }

    private static void AssertThrowsExact(Action action, string expectedMessage)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error)
        {
            Require(
                error.Message == expectedMessage,
                $"expected exact '{expectedMessage}', got '{error.Message}'");
            return;
        }
        throw new InvalidOperationException(
            $"expected InvalidOperationException exactly '{expectedMessage}'");
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + Math.Max(1, start.Length), StringComparison.Ordinal);
        Require(from >= 0 && to > from, $"source slice not found: {start} .. {end}");
        return source[from..to];
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return File.ReadAllText(direct);
            var nested = Path.Combine(directory.FullName, "tdtd-be", relative);
            if (File.Exists(nested))
                return File.ReadAllText(nested);
        }
        throw new FileNotFoundException(relativePath);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
