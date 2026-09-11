using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class WorkReportLifecycleCommandContractTests
{
    public static void Run()
    {
        HashesNormalizedLifecycleSemantics();
        RejectsMissingAndStaleTokens();
        AcceptsOnlyExactCompletedReplay();
        CompletionAdvancesLifecycleWithoutChangingPayloadRevision();
        ReviewerMutationsUseConditionalCommitAndMonotonicSectionSync();
        DirectProjectionDispatchRequiresExactApprovalBoundary();
    }

    private static void HashesNormalizedLifecycleSemantics()
    {
        var left = new
        {
            ExpectedPayloadRevision = 7,
            ExpectedLifecycleRevision = 3,
            CommandId = " command-001 ",
            Comment = "  approved  ",
            PayloadJson = "{\"z\":2,\"a\":1}"
        };
        var right = new
        {
            PayloadJson = " { \"a\" : 1, \"z\" : 2 } ",
            Comment = "approved",
            CommandId = "different-command-002",
            ExpectedLifecycleRevision = 99,
            ExpectedPayloadRevision = 101
        };
        var changed = new
        {
            PayloadJson = "{\"a\":1,\"z\":3}",
            Comment = "approved"
        };

        var leftHash = WorkReportLifecycleCommandContract.ComputeHash("REVIEW_APPROVE", left);
        var rightHash = WorkReportLifecycleCommandContract.ComputeHash("REVIEW_APPROVE", right);
        var changedHash = WorkReportLifecycleCommandContract.ComputeHash("REVIEW_APPROVE", changed);

        Equal(leftHash, rightHash, "protocol metadata, whitespace, and JSON object order are not semantic");
        NotEqual(leftHash, changedHash, "changed nested payload remains semantic");
    }

    private static void RejectsMissingAndStaleTokens()
    {
        var report = Report();

        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_REQUIRED,
            () => Resolve(report, null, 7, "review-command-001", "hash-a"));
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_REQUIRED,
            () => Resolve(report, 3, null, "review-command-001", "hash-a"));
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_COMMAND_ID_REQUIRED,
            () => Resolve(report, 3, 7, "short", "hash-a"));
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
            () => Resolve(report, 2, 7, "review-command-001", "hash-a"));
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT,
            () => Resolve(report, 3, 6, "review-command-001", "hash-a"));
    }

    private static void AcceptsOnlyExactCompletedReplay()
    {
        var report = Report();
        report.LifecycleRevision = 4;
        report.LastLifecycleCommandId = "review-command-001";
        report.LastLifecycleCommandHash = "hash-a";
        report.LastLifecycleCommandOperation = "REVIEW_APPROVE";
        report.LastLifecycleCommandRevision = 4;
        report.LastLifecycleCommandPayloadRevision = 7;
        report.LastLifecycleCommandStatus = WorkAssignmentReportStatus.Submitted;
        report.LastLifecycleCommandIsActive = true;

        var (_, resolution) = Resolve(report, 3, 7, " review-command-001 ", "hash-a");
        Equal(WorkReportLifecycleCommandResolution.CompletedReplay, resolution, "exact replay resolution");

        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_COMMAND_REPLAY_MISMATCH,
            () => Resolve(report, 3, 7, "review-command-001", "hash-b"));
    }

    private static void CompletionAdvancesLifecycleWithoutChangingPayloadRevision()
    {
        var report = Report();
        var (command, resolution) = Resolve(report, 3, 7, "review-command-001", "hash-a");
        Equal(WorkReportLifecycleCommandResolution.NewCommand, resolution, "new command resolution");

        WorkReportLifecycleCommandContract.ApplyCompletionInMemory(
            report,
            command,
            WorkAssignmentReportStatus.Approved,
            resultIsActive: true);

        Equal(4, report.LifecycleRevision, "lifecycle revision");
        Equal(7, report.PayloadRevision, "payload revision");
        Equal(4, report.LastLifecycleCommandRevision, "last lifecycle command revision");
        Equal(7, report.LastLifecycleCommandPayloadRevision, "last command payload revision");
        Equal(WorkAssignmentReportStatus.Approved, report.LastLifecycleCommandStatus, "last command status");
    }

    private static void ReviewerMutationsUseConditionalCommitAndMonotonicSectionSync()
    {
        var source = ReadBackendSource("Services/WorkAssignments/Review/WorkAssignmentReviewService.cs");
        foreach (var operation in new[]
                 {
                     "REVIEW_APPROVE",
                     "REVIEW_RETURN",
                     "REVIEW_RECALL_APPROVED",
                     "REVIEW_DEACTIVATE_REPORT",
                     "REVIEW_REACTIVATE_REPORT"
                 })
        {
            Contains(source, operation, $"review operation {operation}");
        }

        var commit = Slice(
            source,
            "private async Task<bool> TryCommitLifecycleCommandAsync(",
            "private async Task SyncReportSectionsAfterLifecycleCommitAsync(");
        Contains(commit, "WorkReportLifecycleCommandContract.BuildCommitFilter", "status/active/revision commit filter");
        Contains(commit, "_transactions.ExecuteAsync(", "report, outbox, and Work fence transaction");
        Contains(commit, "reportResult.ModifiedCount != 1", "successful conditional commit check");
        Equal(
            1,
            CountOccurrences(commit, "WorkDirectSourceRevisionFence.IncrementAsync("),
            "exactly one Work Direct-source fence per successful reviewer commit");
        Contains(commit, "WorkReportLifecycleCommandContract.IsCompletedReplay", "concurrent exact replay handling");
        Contains(commit, "WorkReportLifecycleCommandContract.Conflict", "concurrent conflict handling");
        Contains(commit, "lifecycleSeriesLease.RenewAsync", "series lease ownership is renewed before report CAS");

        var sectionSync = Slice(
            source,
            "private async Task SyncReportSectionsAfterLifecycleCommitAsync(",
            "private static System.Linq.Expressions.Expression<Func<ReviewReportListDocRole, ReviewReportFlatRowDto>>");
        Contains(sectionSync, "fb.Lte(x => x.SourceLifecycleRevision, report.LifecycleRevision)", "monotonic section lifecycle filter");
        Contains(sectionSync, ".Set(x => x.Status, report.Status)", "section status synchronization");
        Contains(sectionSync, ".Set(x => x.SourceLifecycleRevision, report.LifecycleRevision)", "section lifecycle revision synchronization");

        var leaseSource = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleSeriesLockService.cs");
        Contains(leaseSource, "public async Task RenewAsync", "renewable assignment-series lease");
        Contains(leaseSource, "SERIES_LEASE_LOST", "stable lost-lease conflict");
        DoesNotContain(
            leaseSource,
            ".Inc(x => x.ReportLifecycleSeriesRevision",
            "failed validation must not persist a series revision write");
    }

    private static void DirectProjectionDispatchRequiresExactApprovalBoundary()
    {
        var source = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs");
        var resolver = Slice(
            source,
            "ResolveCurrentApprovedDirectProjectionEntry(",
            "private async Task<LifecycleProjectionClaim?> TryClaimAsync(");
        Contains(
            resolver,
            """string.Equals(entry.FromStatus, "SUBMITTED", StringComparison.Ordinal)""",
            "Direct dispatch must reject APPROVED-to-APPROVED auto-confirm entries");
        Contains(
            resolver,
            """string.Equals(entry.ToStatus, "APPROVED", StringComparison.Ordinal)""",
            "Direct dispatch must require the approved target state");
        Contains(
            resolver,
            "entry.LifecycleRevision == report.LifecycleRevision",
            "Direct dispatch must bind the current lifecycle revision");
    }

    private static (WorkReportLifecycleCommand Command, WorkReportLifecycleCommandResolution Resolution) Resolve(
        WorkAssignmentReport report,
        int? expectedLifecycleRevision,
        int? expectedPayloadRevision,
        string? commandId,
        string commandHash)
        => WorkReportLifecycleCommandContract.Resolve(
            report,
            expectedLifecycleRevision,
            expectedPayloadRevision,
            commandId,
            "REVIEW_APPROVE",
            commandHash);

    private static WorkAssignmentReport Report()
        => new()
        {
            Id = "1000000000000000000000a1",
            Status = WorkAssignmentReportStatus.Submitted,
            IsActive = true,
            PayloadRevision = 7,
            LifecycleRevision = 3
        };

    private static void Code(AppErrorCode expected, Action action)
    {
        var error = Throws<AppException>(action);
        Equal(expected, error.Code, "application error code");
    }

    private static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

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

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = startIndex < 0 ? -1 : source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
            throw new InvalidOperationException($"Source contract anchors were not found: {start} -> {end}");

        return source[startIndex..endIndex];
    }

    private static void Contains(string source, string expected, string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}'.");
    }

    private static void DoesNotContain(string source, string unexpected, string context)
    {
        if (source.Contains(unexpected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: did not expect '{unexpected}'.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void NotEqual<T>(T unexpected, T actual, string context)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new InvalidOperationException($"{context}: did not expect '{actual}'.");
    }
}
