using MongoDB.Bson.Serialization;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignments.Internal;

internal static class P9BasicSummaryContractTests
{
    public static void Run()
    {
        CandidateRouteAndFourScopesAreExact();
        LockedConfigIsRuntimeAuthority();
        ApprovedOnlyMembershipIsBoundedAndDeterministic();
        SnapshotAndResultCarryLineagePins();
    }

    private static void CandidateRouteAndFourScopesAreExact()
    {
        Assert(
            StatRunRouteRegistry.Snapshot()[StatRunRouteRegistry.BasicResult] ==
            StatRunCapabilities.BasicSummary,
            "Basic result route must bind BASIC_SUMMARY.");
        Assert(StatRunRouteRegistry.MinimumStage(StatRunRouteRegistry.BasicResult) == 2,
            "Basic result must require stage 2.");
        var scopes = new[]
        {
            WorkAssignmentSummarySourceScope.FlowBranch,
            WorkAssignmentSummarySourceScope.FlowStep,
            WorkAssignmentSummarySourceScope.FlowEffectivePath,
            WorkAssignmentSummarySourceScope.FlowFinal
        };
        Assert(scopes.SequenceEqual(new[]
        {
            "FLOW_BRANCH", "FLOW_STEP", "FLOW_EFFECTIVE_PATH", "FLOW_FINAL"
        }, StringComparer.Ordinal), "Frozen Flow scope names drifted.");
        Assert(scopes.All(WorkAssignmentSummarySourceScope.IsFlowMode),
            "All frozen scopes must be recognized as Flow scopes.");
        Assert(!StatRunRouteRegistry.Snapshot().ContainsKey("FLOW_STATISTIC_PROFILE"),
            "Flow statistic profile must remain blocked.");
    }

    private static void LockedConfigIsRuntimeAuthority()
    {
        var runtime = ReadBackend(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P9.Runtime.cs");
        foreach (var token in new[]
                 {
                     "P804LoadStateAsync",
                     "StatConfigStatuses.Locked",
                     "BASIC_SUMMARY_LOCKED_CONFIG_REQUIRED",
                     "BASIC_SUMMARY_MEMBERSHIP_SPOOF_REJECTED",
                     "payload.Targets",
                     "NormalizeRules",
                     "payload.PeriodRule",
                     "payload.DetailHints",
                     "StatRunCapabilities.FlowScopes"
                 })
        {
            Assert(runtime.Contains(token, StringComparison.Ordinal),
                $"Locked runtime token missing: {token}.");
        }
        Assert(!runtime.Contains("FLOW_STATISTIC_PROFILE", StringComparison.Ordinal),
            "Runtime must not enable Flow statistic profile.");
    }

    private static void ApprovedOnlyMembershipIsBoundedAndDeterministic()
    {
        var service = ReadBackend(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.cs");
        foreach (var token in new[]
                 {
                     "WorkAssignmentReportStatus.Approved",
                     "x.IsCurrent, true",
                     "WorkReportCumulativeContributionMode.Exclude",
                     "MaxSourceReportsPerSummary + 1",
                     "BuildSourceSignatureHash",
                     "SnapshotDirty",
                     ".SortBy(x => x.WorkAssignmentId)",
                     ".ThenBy(x => x.PeriodKey)"
                 })
        {
            Assert(service.Contains(token, StringComparison.Ordinal),
                $"Approved/bounded source token missing: {token}.");
        }
        var scope = ReadBackend(
            "Services/WorkAssignments/Internal/" +
            "WorkAssignmentSummarySourceScope.cs");
        Assert(scope.Contains(".Distinct(StringComparer.Ordinal)", StringComparison.Ordinal),
            "Membership filters must deduplicate client intent.");
        Assert(scope.Contains(".SortBy(x => x.Path)", StringComparison.Ordinal),
            "Membership ordering must be deterministic.");
    }

    private static void SnapshotAndResultCarryLineagePins()
    {
        var map = BsonClassMap.LookupClassMap(
            typeof(WorkAssignmentBasicSummarySnapshot));
        foreach (var property in new[]
                 {
                     nameof(WorkAssignmentBasicSummarySnapshot.ConfigVersionId),
                     nameof(WorkAssignmentBasicSummarySnapshot.ConfigRevision),
                     nameof(WorkAssignmentBasicSummarySnapshot.ConfigHash),
                     nameof(WorkAssignmentBasicSummarySnapshot.ConfigDependencyPins),
                     nameof(WorkAssignmentBasicSummarySnapshot.CandidateChainId),
                     nameof(WorkAssignmentBasicSummarySnapshot.CandidatePromptId),
                     nameof(WorkAssignmentBasicSummarySnapshot.CandidateStage),
                     nameof(WorkAssignmentBasicSummarySnapshot.CandidateCatalogRawSha256),
                     nameof(WorkAssignmentBasicSummarySnapshot.CandidateStageLockSha256),
                     nameof(WorkAssignmentBasicSummarySnapshot.SourceSignatureHash),
                     nameof(WorkAssignmentBasicSummarySnapshot.RefreshJobId)
                 })
        {
            Assert(map.GetMemberMap(property) is not null,
                $"Snapshot lineage member missing: {property}.");
        }
        foreach (var property in new[]
                 {
                     nameof(WorkAssignmentBasicSummaryMetaDto.ConfigVersionId),
                     nameof(WorkAssignmentBasicSummaryMetaDto.ConfigHash),
                     nameof(WorkAssignmentBasicSummaryMetaDto.CandidateChainId),
                     nameof(WorkAssignmentBasicSummaryMetaDto.CandidateStageLockSha256),
                     nameof(WorkAssignmentBasicSummaryMetaDto.SourceSignatureHash),
                     nameof(WorkAssignmentBasicSummaryMetaDto.CalculationJobId)
                 })
        {
            Assert(typeof(WorkAssignmentBasicSummaryMetaDto).GetProperty(property) is not null,
                $"Result lineage member missing: {property}.");
        }
    }

    private static string ReadBackend(string relativePath)
        => File.ReadAllText(Path.Combine(FindBackendRoot(), relativePath));

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tdtd-be.csproj")))
                return directory.FullName;
            var nested = Path.Combine(directory.FullName, "tdtd-be");
            if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                return nested;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate tdtd-be source root.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
