using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using tdtd_be.Common.Errors;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;

internal static class DynamicFlowMappingP708BarrierContractTests
{
    public static void Run()
    {
        ThroughEightSeparatesApplyFromLifecycle();
        StableBarrierFailsClosedUntilP709();
        EveryMappingLifecycleWriterCrossesTheBarrier();
    }

    private static void ThroughEightSeparatesApplyFromLifecycle()
    {
        var throughEight =
            WorkAssignmentReportService.ResolveDynamicFlowMappingActivation(
                runtimeEnabled: true,
                configuredThrough: 8);
        Require(
            throughEight.CanApply &&
            !throughEight.CanLifecycleMapping,
            "P7-08 must enable durable apply while keeping lifecycle mapping blocked");

        var throughNine =
            WorkAssignmentReportService.ResolveDynamicFlowMappingActivation(
                runtimeEnabled: true,
                configuredThrough: 9);
        Require(
            throughNine.CanApply &&
            throughNine.CanLifecycleMapping,
            "P7-09 must preserve apply and enable lifecycle mapping");

        DynamicFlowRuntimeActivationPolicy Policy(int through)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [DynamicFlowRuntimeActivationPolicy
                            .TestingP7MappingActivationKey] = "true",
                        [DynamicFlowRuntimeActivationPolicy
                            .TestingP7MappingActivationThroughKey] =
                            through.ToString(
                                System.Globalization.CultureInfo
                                    .InvariantCulture)
                    })
                .Build();
            return new DynamicFlowRuntimeActivationPolicy(
                new TestHostEnvironment("Testing"),
                configuration,
                sealedP6CatalogActivationEnabled: false);
        }

        var runtimeThroughEight = Policy(8);
        var runtimeThroughNine = Policy(9);
        Require(
            runtimeThroughEight.P7MappingCandidateExecutionEnabled &&
            !runtimeThroughEight.P7MappingLifecycleExecutionEnabled,
            "shared runtime activation must keep P7-08 mapping lifecycle closed");
        Require(
            runtimeThroughNine.P7MappingCandidateExecutionEnabled &&
            runtimeThroughNine.P7MappingLifecycleExecutionEnabled,
            "shared runtime activation must open mapping lifecycle at P7-09");
    }

    private static void StableBarrierFailsClosedUntilP709()
    {
        AppException? blocked = null;
        try
        {
            DynamicFlowMappingLifecycleContract.EnsureP7LifecyclePhase(
                canLifecycleMapping: false,
                operation: "SUBMIT",
                reason:
                    DynamicFlowMappingLifecycleContract
                        .LifecycleBlockedReason,
                reportId: "507f1f77bcf86cd799439011");
        }
        catch (AppException error)
        {
            blocked = error;
        }

        Require(blocked is not null, "through=8 must throw the P7-09 barrier");
        Require(
            blocked.Code ==
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            "mapping lifecycle must reuse the stable execution phase error");
        var details = JsonSerializer.Serialize(blocked.Details);
        Require(
            details.Contains(
                DynamicFlowMappingLifecycleContract
                    .LifecycleBlockedReason,
                StringComparison.Ordinal) &&
            details.Contains(
                DynamicFlowMappingLifecycleContract
                    .LifecycleBlockedUntilPhase,
                StringComparison.Ordinal),
            "phase error must expose the exact reason and P7-09 target");

        DynamicFlowMappingLifecycleContract.EnsureP7LifecyclePhase(
            canLifecycleMapping: true,
            operation: "SUBMIT",
            reason:
                DynamicFlowMappingLifecycleContract
                    .LifecycleBlockedReason);
    }

    private static void EveryMappingLifecycleWriterCrossesTheBarrier()
    {
        var lifecyclePartial = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingLifecycle.cs");
        AssertBefore(
            lifecyclePartial,
            "RerunBlockedReason",
            "BuildSuccessorPlan(",
            "rerun must cross the P7-09 barrier before successor planning");
        Require(
            lifecyclePartial.Contains(
                "CanLifecycleMapping",
                StringComparison.Ordinal),
            "report lifecycle wrapper must consume staged activation");
        Require(
            lifecyclePartial.Contains(
                "EnsureSourceMutationPhaseAsync(",
                StringComparison.Ordinal),
            "report lifecycle wrapper must preflight mapped source dependents before mutation");

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        foreach (var member in new[]
                 {
                     "SubmitAsync(",
                     "AcceptAsync(",
                     "ReturnAsync(",
                     "WithdrawSubmittedAsync("
                 })
        {
            var body = MemberBody(reportService, member);
            AssertBefore(
                body,
                "ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(",
                "CommitLegacyLifecycleWithDirectSourceFenceAsync(",
                $"{member} must fail before its report write");
        }
        var legacyLifecycleCommit = MemberBody(
            reportService,
            "private async Task<UpdateResult> CommitLegacyLifecycleWithDirectSourceFenceAsync(");
        Require(
            legacyLifecycleCommit.Contains(
                "_dynamicFlowTransactions.ExecuteAsync(",
                StringComparison.Ordinal),
            "legacy lifecycle report CAS, durable outbox, and Work fence must share one transaction");
        AssertBefore(
            legacyLifecycleCommit,
            "UpdateOneAsync(",
            "WorkDirectSourceRevisionFence.IncrementAsync(",
            "legacy lifecycle Work fence must follow a successful report CAS");
        Require(
            Count(
                legacyLifecycleCommit,
                "WorkDirectSourceRevisionFence.IncrementAsync(") == 1,
            "legacy lifecycle transaction must increment exactly one Work Direct-source fence");
        Require(
            MemberBody(reportService, "SaveDraftAsync(").Contains(
                "ValidateDynamicFlowMappingLifecycleBoundaryAsync(",
                StringComparison.Ordinal),
            "manual draft save must preserve provenance without requiring P7-09");

        var reviewService = ReadBackendSource(
            "Services/WorkAssignments/Review/WorkAssignmentReviewService.cs");
        var approve = MemberBody(
            reviewService,
            "ApproveReportAsync(");
        AssertBefore(
            approve,
            "P7MappingLifecycleExecutionEnabled",
            "report.Status != WorkAssignmentReportStatus.Submitted",
            "mapped approve must hit the P7-09 barrier before ordinary status validation");
        var reviewCommit = MemberBody(
            reviewService,
            "private async Task<bool> TryCommitLifecycleCommandAsync(");
        AssertBefore(
            reviewCommit,
            "P7MappingLifecycleExecutionEnabled",
            "UpdateOneAsync(",
            "approve/return/recall must fail before review CAS");
        AssertBefore(
            reviewCommit,
            "EnsureSourceMutationPhaseAsync(",
            "UpdateOneAsync(",
            "source lifecycle mutation must hit the P7-09 invalidation barrier before review CAS");

        var lifecycleContract = ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowMappingLifecycleContract.cs");
        var sourceInvalidation = MemberBody(
            lifecycleContract,
            "InvalidateSourceDependentsAsync(");
        AssertBefore(
            sourceInvalidation,
            "if (!canLifecycleMapping)",
            "transactions.ExecuteAsync",
            "source invalidation must fail or no-op before its transaction");

        var runtimeLifecycle = ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowRuntimeMappingLifecycle.cs");
        var epochInvalidation = MemberBody(
            runtimeLifecycle,
            "InvalidateDynamicFlowMappingProvenanceAsync(");
        AssertBefore(
            epochInvalidation,
            "EnsureP7LifecyclePhase(",
            "UpdateManyAsync(",
            "epoch invalidation must fail before its provenance write");

        var epochCommands = ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowRuntimeEpochCommands.cs");
        Require(
            MemberBody(
                    epochCommands,
                    "private async Task<DynamicFlowEpochCommandResponse>")
                .Contains(
                    "P7MappingLifecycleExecutionEnabled",
                    StringComparison.Ordinal),
            "epoch command must pass the shared P7-09 activation state");
    }

    private static string MemberBody(string source, string member)
    {
        var start = source.IndexOf(member, StringComparison.Ordinal);
        Require(start >= 0, $"missing source member {member}");
        var next = source.IndexOf(
            "\n    public ",
            start + member.Length,
            StringComparison.Ordinal);
        if (next < 0)
        {
            next = source.IndexOf(
                "\n    private ",
                start + member.Length,
                StringComparison.Ordinal);
        }

        return next < 0
            ? source[start..]
            : source[start..next];
    }

    private static void AssertBefore(
        string source,
        string first,
        string second,
        string message)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Require(
            firstIndex >= 0 &&
            secondIndex >= 0 &&
            firstIndex < secondIndex,
            message);
    }

    private static int Count(string source, string value)
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

    private static string ReadBackendSource(string relativePath)
    {
        var path = Path.Combine(
            FindBackendRoot(),
            relativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Require(File.Exists(path), $"backend source was not found: {relativePath}");
        return File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindBackendRoot()
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var cursor = new DirectoryInfo(seed);
            while (cursor is not null)
            {
                var direct = Path.Combine(cursor.FullName, "tdtd-be.csproj");
                if (File.Exists(direct))
                    return cursor.FullName;
                var nested = Path.Combine(
                    cursor.FullName,
                    "tdtd-be",
                    "tdtd-be.csproj");
                if (File.Exists(nested))
                    return Path.GetDirectoryName(nested)!;
                cursor = cursor.Parent;
            }
        }

        throw new InvalidOperationException("tdtd-be root was not found");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TestHostEnvironment(string environmentName)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tdtd-be.Tests";
        public string ContentRootPath { get; set; } =
            Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
