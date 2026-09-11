using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class WorkReportLifecycleProjectionOutboxContractTests
{
    public static void Run()
    {
        EntryKeyAndPendingEnvelopeAreDeterministic();
        PayloadOnlyEntriesCanShareLifecycleRevisionSafely();
        BusinessEventsAreDurableAndDeterministic();
        PeriodProjectionUsesCurrentReportState();
        PeriodProjectionPreservesLifecycleOwnedFieldsAndTransitionSemantics();
        DirtyRangePreservesApprovedAndActiveImpact();
        AggregateDependentRecoveryGatesAndCoalescesTransitions();
        AggregateRecoveryExecutionScopeIsNestedAndRestored();
        DirectProjectionKeepsUnconfiguredStatisticsOutOfScope();
        ReconcilerSourceKeepsLeaseFailureAndMonotonicContracts();
        RecurringWorkerAndDependencyInjectionAreRegistered();
    }

    private static void EntryKeyAndPendingEnvelopeAreDeterministic()
    {
        var createdAt = new DateTime(2026, 7, 22, 1, 2, 3, DateTimeKind.Utc);
        var seed = new WorkReportLifecycleOutboxSeed(
            "command-report-001",
            8,
            "review_approve",
            "100000000000000000000001",
            "submitted",
            "approved",
            true,
            true,
            11,
            "payload-hash",
            createdAt);

        var first = WorkReportLifecycleOutboxContract.CreateEntry(seed);
        var second = WorkReportLifecycleOutboxContract.CreateEntry(seed with { CreatedAtUtc = createdAt.AddMinutes(5) });
        Equal(first.EntryKey, second.EntryKey, "entry key must ignore attempt time");
        Equal(64, first.EntryKey.Length, "entry key SHA-256 length");
        Equal(WorkReportLifecycleProjectionOutboxStates.Pending, first.State, "initial state");
        Equal("REVIEW_APPROVE", first.Operation, "canonical operation");
        Equal("SUBMITTED", first.FromStatus, "canonical from status");
        Equal("APPROVED", first.ToStatus, "canonical to status");
        Equal(8, first.LifecycleRevision, "captured lifecycle revision");
        Equal(11, first.PayloadRevision, "captured payload revision");
        Equal(0, first.AttemptCount, "initial attempt count");
        NotEqual(
            first.EntryKey,
            WorkReportLifecycleOutboxContract.ComputeEntryKey(seed.CommandId, 9, seed.Operation),
            "revision participates in deterministic key");
    }

    private static void PayloadOnlyEntriesCanShareLifecycleRevisionSafely()
    {
        var createdAt = new DateTime(2026, 7, 22, 1, 15, 0, DateTimeKind.Utc);
        var first = WorkReportLifecycleOutboxContract.CreateEntry(new WorkReportLifecycleOutboxSeed(
            "save-draft-command-001",
            0,
            "SAVE_DRAFT",
            "100000000000000000000001",
            "DRAFT",
            "DRAFT",
            true,
            true,
            1,
            "payload-hash-1",
            createdAt));
        var second = WorkReportLifecycleOutboxContract.CreateEntry(new WorkReportLifecycleOutboxSeed(
            "save-draft-command-002",
            0,
            "SAVE_DRAFT",
            "100000000000000000000001",
            "DRAFT",
            "DRAFT",
            true,
            true,
            2,
            "payload-hash-2",
            createdAt.AddMinutes(1)));

        Equal(0, first.LifecycleRevision, "initial draft lifecycle revision is valid");
        Equal(first.LifecycleRevision, second.LifecycleRevision, "payload-only commits share lifecycle revision");
        NotEqual(first.EntryKey, second.EntryKey, "command id keeps same-revision entries distinct");
        NotEqual(first.BusinessEvents[0].EventKey, second.BusinessEvents[0].EventKey, "same-revision log events remain distinct");
        Equal("SAVE_DRAFT", first.BusinessEvents[0].ReportLogAction, "draft save report audit action");
        Equal<string?>(null, first.BusinessEvents[0].UserAction, "draft save does not invent a user action type");
    }

    private static void BusinessEventsAreDurableAndDeterministic()
    {
        var seed = new WorkReportLifecycleOutboxSeed(
            "auto-submit-command-001",
            4,
            "SUBMIT",
            "100000000000000000000001",
            "DRAFT",
            "APPROVED",
            true,
            true,
            9,
            "payload-hash",
            new DateTime(2026, 7, 22, 1, 30, 0, DateTimeKind.Utc),
            BusinessComment: "late reason",
            SecondaryBusinessReason: "AUTO_APPROVE_CONDITION",
            SecondaryBusinessComment: "auto-approved",
            BusinessSnapshotJson: "{\"matched\":true}");

        var first = WorkReportLifecycleOutboxContract.CreateEntry(seed);
        var replay = WorkReportLifecycleOutboxContract.CreateEntry(
            seed with { CreatedAtUtc = seed.CreatedAtUtc.AddHours(1) });

        Equal(2, first.BusinessEvents.Count, "auto-approved submit persists both business events");
        Equal("SUBMIT", first.BusinessEvents[0].ReportLogAction, "submit audit event");
        Equal("AUTO_APPROVE", first.BusinessEvents[1].ReportLogAction, "auto-approve audit event");
        Equal(first.BusinessEvents[0].EventKey, replay.BusinessEvents[0].EventKey, "event key ignores attempt time");
        Equal(first.BusinessEvents[1].EventKey, replay.BusinessEvents[1].EventKey, "secondary event key is deterministic");
        Equal(24, WorkReportLifecycleOutboxContract.ComputeStableObjectId(first.BusinessEvents[0].EventKey).Length,
            "event key maps to stable Mongo ObjectId text");
    }

    private static void PeriodProjectionUsesCurrentReportState()
    {
        var now = new DateTime(2026, 7, 22, 2, 0, 0, DateTimeKind.Utc);
        var period = new WorkReportPeriod
        {
            Id = "100000000000000000000011",
            DueAtUtc = now.AddHours(-1),
            Status = WorkReportPeriodStatus.Submitted,
            CurrentReportId = "100000000000000000000012"
        };
        var staleTrigger = Report("100000000000000000000012", WorkAssignmentReportStatus.Submitted, false, now.AddMinutes(-5));
        var current = Report("100000000000000000000013", WorkAssignmentReportStatus.Approved, true, now.AddMinutes(-1));
        current.IsLateSubmission = true;
        current.ApprovedAtUtc = now.AddMinutes(-1);

        var projected = WorkReportLifecycleProjectionPolicy.ResolvePeriod(period, current, staleTrigger, now);
        Equal(current.Id, projected.CurrentReportId, "authoritative current report id");
        Equal(WorkReportPeriodStatus.OverdueApproved, projected.Status, "current approved status");
        Equal(current.ApprovedAtUtc, projected.LastReviewedAtUtc, "current review timestamp");

        var empty = WorkReportLifecycleProjectionPolicy.ResolvePeriod(period, null, staleTrigger, now);
        Equal<string?>(null, empty.CurrentReportId, "inactive stale report must not remain current");
        Equal(WorkReportPeriodStatus.OverduePending, empty.Status, "empty overdue period state");
    }

    private static void PeriodProjectionPreservesLifecycleOwnedFieldsAndTransitionSemantics()
    {
        var now = new DateTime(2026, 7, 22, 3, 0, 0, DateTimeKind.Utc);
        var previousReview = now.AddDays(-1);
        var period = new WorkReportPeriod
        {
            Id = "100000000000000000000031",
            DueAtUtc = now.AddDays(2),
            LastReviewedAtUtc = previousReview,
            AcceptedLateReason = "accepted-before",
            Status = WorkReportPeriodStatus.Submitted
        };
        var report = Report("100000000000000000000032", WorkAssignmentReportStatus.Submitted, true, now);
        report.StartedDate = now.AddDays(-3);
        report.CompletedDate = now.AddDays(-2);
        report.DueAtUtc = now.AddDays(2);
        report.IsHistoricalData = true;
        report.HistoricalDataApproved = true;
        report.HistoricalDataApprovedAtUtc = now.AddHours(-2);
        report.HistoricalDataApprovedByUserId = "100000000000000000000033";
        report.LifecycleProjectionOutbox = new List<WorkReportLifecycleProjectionOutboxEntry>
        {
            Entry(
                report.LifecycleRevision,
                "APPROVED",
                "SUBMITTED",
                true,
                true,
                "REVIEW_RECALL_APPROVED")
        };

        var recall = WorkReportLifecycleProjectionPolicy.ResolvePeriod(period, report, report, now);
        Equal(now, recall.LastReviewedAtUtc, "recall review timestamp comes from lifecycle commit");
        Equal(report.StartedDate, recall.StartedDate, "started date is repaired from report");
        Equal(report.CompletedDate, recall.CompletedDate, "completed date is repaired from report");
        Equal(true, recall.IsHistoricalData, "historical flag is repaired from report");
        Equal(true, recall.HistoricalDataApproved, "historical approval is repaired from report");
        Equal(report.HistoricalDataApprovedAtUtc, recall.HistoricalDataApprovedAtUtc, "historical approval timestamp");
        Equal(report.HistoricalDataApprovedByUserId, recall.HistoricalDataApprovedByUserId, "historical approval actor");
        Equal("accepted-before", recall.AcceptedLateReason, "recall preserves accepted late reason");

        report.LifecycleProjectionOutbox = new List<WorkReportLifecycleProjectionOutboxEntry>
        {
            Entry(report.LifecycleRevision, "DRAFT", "SUBMITTED", true, true, "SUBMIT")
        };
        var submit = WorkReportLifecycleProjectionPolicy.ResolvePeriod(period, report, report, now);
        Equal(previousReview, submit.LastReviewedAtUtc, "normal submit preserves prior review timestamp");

        report.LifecycleProjectionOutbox = new List<WorkReportLifecycleProjectionOutboxEntry>
        {
            Entry(
                report.LifecycleRevision,
                "APPROVED",
                "SUBMITTED",
                true,
                true,
                "AUTO_AGGREGATE_REVIEW_INVALIDATED")
        };
        var invalidated = WorkReportLifecycleProjectionPolicy.ResolvePeriod(period, report, report, now);
        Equal<DateTime?>(null, invalidated.LastReviewedAtUtc, "aggregate invalidation clears review timestamp");

        report.Status = WorkAssignmentReportStatus.Draft;
        report.ReturnedAtUtc = now.AddMinutes(1);
        report.LifecycleProjectionOutbox = new List<WorkReportLifecycleProjectionOutboxEntry>
        {
            Entry(report.LifecycleRevision, "SUBMITTED", "DRAFT", true, true, "REVIEW_RETURN")
        };
        var returned = WorkReportLifecycleProjectionPolicy.ResolvePeriod(period, report, report, now);
        Equal(report.ReturnedAtUtc, returned.LastReviewedAtUtc, "return uses canonical returned timestamp");
    }

    private static void DirtyRangePreservesApprovedAndActiveImpact()
    {
        var report = Report("100000000000000000000021", WorkAssignmentReportStatus.Submitted, true, DateTime.UtcNow);
        var pending = new List<WorkReportLifecycleProjectionOutboxEntry>
        {
            Entry(3, "SUBMITTED", "APPROVED", true, true),
            Entry(4, "APPROVED", "APPROVED", true, false),
            Entry(5, "APPROVED", "SUBMITTED", false, true)
        };

        var range = WorkReportLifecycleProjectionPolicy.ResolveDirtyRange(pending, report);
        Equal("Approved", range.FromStatus, "approved impact must survive coalescing");
        Equal("Submitted", range.ToStatus, "range ends at current DB status");
        Equal("LIFECYCLE_OUTBOX_RECONCILE:3-5", range.Operation, "coalesced revision range");
    }

    private static void AggregateDependentRecoveryGatesAndCoalescesTransitions()
    {
        var submittedToDraft = Entry(2, "SUBMITTED", "DRAFT", true, true);
        var approvedNoOp = Entry(3, "APPROVED", "APPROVED", true, true);
        var approved = Entry(4, "SUBMITTED", "APPROVED", true, true);
        var deactivated = Entry(5, "SUBMITTED", "SUBMITTED", true, false);
        var completedNewer = Entry(6, "APPROVED", "SUBMITTED", true, true);
        completedNewer.State = WorkReportLifecycleProjectionOutboxStates.Completed;

        Equal(false, WorkReportAggregateDependentRecoveryPolicy.ShouldRecover(submittedToDraft), "non-approved status change");
        Equal(false, WorkReportAggregateDependentRecoveryPolicy.ShouldRecover(approvedNoOp), "approved status no-op");
        Equal(true, WorkReportAggregateDependentRecoveryPolicy.ShouldRecover(approved), "enter approved boundary");
        Equal(true, WorkReportAggregateDependentRecoveryPolicy.ShouldRecover(deactivated), "active boundary");
        Equal(
            5,
            WorkReportAggregateDependentRecoveryPolicy.ResolveTargetLifecycleRevision(
                new[] { submittedToDraft, approvedNoOp, approved, deactivated, completedNewer }),
            "latest pending aggregate-impacting revision");
        Equal<int?>(
            null,
            WorkReportAggregateDependentRecoveryPolicy.ResolveTargetLifecycleRevision(
                new[] { submittedToDraft, approvedNoOp, completedNewer }),
            "no aggregate-impacting pending transition");
    }

    private static void AggregateRecoveryExecutionScopeIsNestedAndRestored()
    {
        Equal(false, WorkReportAggregateDependentRecoveryExecution.IsActive, "recovery scope starts inactive");
        using (WorkReportAggregateDependentRecoveryExecution.Enter())
        {
            Equal(true, WorkReportAggregateDependentRecoveryExecution.IsActive, "outer recovery scope is active");
            using (WorkReportAggregateDependentRecoveryExecution.Enter())
                Equal(true, WorkReportAggregateDependentRecoveryExecution.IsActive, "nested recovery scope remains active");
            Equal(true, WorkReportAggregateDependentRecoveryExecution.IsActive, "disposing nested scope preserves outer scope");
        }

        Equal(false, WorkReportAggregateDependentRecoveryExecution.IsActive, "disposing outer scope restores inactive state");
    }

    private static void DirectProjectionKeepsUnconfiguredStatisticsOutOfScope()
    {
        var source = ReadBackendSource("Services/StatisticsRun/StatRunDirectProjectionService.cs");
        Contains(source, "STATISTICS_NOT_CONFIGURED", "unconfigured statistics terminal zero-write reason");
        Contains(source, "GetP804TrustedPersistedView(template)", "trusted persisted footprint authority");
        Contains(source, "?? throw Fail(\"LOCKED_CONFIG_MISSING\")", "configured callers still require a locked config");
        Before(source, "ValidateSourcePeriod(source.Report, source.Period);", "\"STATISTICS_NOT_CONFIGURED\"", "source period must validate before zero-write");
        Before(source, "\"STATISTICS_NOT_CONFIGURED\"", "ResolveMembershipAsync(", "unconfigured forms must not execute statistics membership");
    }

    private static void ReconcilerSourceKeepsLeaseFailureAndMonotonicContracts()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs");
        var sectionProjection = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkAssignmentReportSectionProjectionService.cs");
        Contains(source, "FindOneAndUpdateAsync", "atomic report-level claim");
        Contains(source, "LifecycleProjectionClaimExpiresAtUtc", "expiring claim lease");
        Contains(source, "Always reload", "current-state reload rationale");
        Contains(source, "_sectionProjection.ProjectCurrentAndVerifyAsync", "canonical complete section repair");
        Contains(
            sectionProjection,
            "fb.Lte(x => x.SourceLifecycleRevision, projection.SourceLifecycleRevision)",
            "monotonic section lifecycle guard");
        Contains(source, "SourceLifecycleReportId", "period report-local revision namespace");
        Contains(source, "WorkReportLifecycleProjectionOutboxStates.Pending", "failed entries remain pending");
        Contains(source, "RecordFailureAndReleaseClaimAsync", "failure release path");
        Contains(source, "renewal.MatchedCount != 1", "claim renewal ownership check");
        Contains(source, "Lifecycle command committed with projection pending", "stable committed-pending foreground path");
        Contains(source, "maxAttempts = 3", "bounded period compare-and-swap repair");
        Contains(source, "MatchesPeriod", "period projection verification before acknowledgement");
        Contains(source, "lifecycleProjectionOutbox.$[entry].state", "atomic entry completion");
        Contains(source, "entry.entryKey", "completion and failure updates target captured entries");
        Contains(source, "attemptedEntryKeys", "same-revision entries are not acknowledged by revision range");
        Contains(source, "RebuildReportPeriodAsync", "doc-role reconciliation");
        Contains(source, "RebuildForReportAsync", "statistics reconciliation");
        Contains(source, "_aggregateDependentRecovery.RecoverPendingAsync", "aggregate-dependent recovery");
        Contains(source, "MarkReportStatusMutationDirtyAsync", "advanced-summary invalidation");
        Before(
            source,
            "_aggregateDependentRecovery.RecoverPendingAsync",
            "CompletePendingEntriesAsync(claim",
            "aggregate recovery must finish before outbox completion");
        Before(
            source,
            "_businessLogProjector.ProjectAndVerifyAsync",
            "CompletePendingEntriesAsync(claim",
            "idempotent business logs must verify before outbox completion");

        var recovery = ReadBackendSource("Services/WorkAssignmentReports/Runtime/WorkReportAggregateDependentRecoveryService.cs");
        var reportService = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var reviewService = ReadBackendSource("Services/WorkAssignments/Review/WorkAssignmentReviewService.cs");
        Contains(recovery, "IServiceProvider", "lazy current-scope report-service resolution");
        Contains(recovery, "AggregateDependentsLastRecoveredLifecycleRevision", "monotonic aggregate recovery marker");
        Contains(recovery, "WorkReportAggregateDependentRecoveryExecution.Enter", "aggregate recovery recursion scope");
        Contains(reportService, "ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync", "nested lifecycle reconciliation guard");
        Contains(reviewService, "_aggregateDependentRecovery.RecoverPendingAsync", "review path delegates aggregate recovery");
    }

    private static void RecurringWorkerAndDependencyInjectionAreRegistered()
    {
        var program = ReadBackendSource("Program.cs");
        var runner = ReadBackendSource("Jobs/NonOverlappingRecurringJobRunner.cs");
        var registrar = ReadBackendSource("Jobs/HangfireRecurringJobRegistrar.cs");
        Contains(program, "IWorkReportLifecycleProjectionReconciler, WorkReportLifecycleProjectionReconciler", "reconciler DI");
        Contains(program, "IWorkReportAggregateDependentRecoveryService, WorkReportAggregateDependentRecoveryService", "aggregate recovery DI");
        Contains(runner, "ProcessWorkReportLifecycleProjectionOutboxAsync", "non-overlapping runner");
        Contains(registrar, "work-report:lifecycle-projection-outbox", "stable recurring job id");
        Contains(registrar, "WorkReportLifecycleProjectionOutbox:Cron", "configurable recurring cron");
    }

    private static WorkAssignmentReport Report(
        string id,
        WorkAssignmentReportStatus status,
        bool active,
        DateTime updatedAtUtc)
        => new()
        {
            Id = id,
            Status = status,
            IsActive = active,
            IsCurrent = active,
            UpdatedAtUtc = updatedAtUtc,
            PayloadRevision = 2,
            LifecycleRevision = 5
        };

    private static WorkReportLifecycleProjectionOutboxEntry Entry(
        int revision,
        string fromStatus,
        string toStatus,
        bool fromActive,
        bool toActive,
        string operation = "TEST")
        => new()
        {
            EntryKey = $"entry-{revision}",
            CommandId = $"command-{revision:00000000}",
            LifecycleRevision = revision,
            Operation = operation,
            State = WorkReportLifecycleProjectionOutboxStates.Pending,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            FromIsActive = fromActive,
            ToIsActive = toActive,
            CreatedAtUtc = DateTime.UnixEpoch
        };

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "tdtd-be.csproj");
                if (File.Exists(direct))
                    return File.ReadAllText(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                var nestedRoot = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nestedRoot, "tdtd-be.csproj")))
                    return File.ReadAllText(Path.Combine(nestedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        throw new InvalidOperationException("Could not locate the tdtd-be source root.");
    }

    private static void Contains(string source, string expected, string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}'.");
    }

    private static void Before(string source, string first, string second, string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
            throw new InvalidOperationException($"{context}: expected '{first}' before '{second}'.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void NotEqual<T>(T left, T right, string context)
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
            throw new InvalidOperationException($"{context}: values must differ.");
    }
}
