using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

internal static class StatConfigBundleP809ContractTests
{
    private const string OwnerId = "000000000000000000000901";
    private static readonly string BundleHash = new('a', 64);

    public static void Run()
    {
        BundleDependencyDomainAndBarrierEntriesAreExact();
        EmptyBundleStatesAndHashAreDeterministicAndDistinct();
        BarrierProbeRequiresActorCommandAndHashShape();
        EveryBarrierEntryReturnsTheStablePhaseContract();
    }

    private static void BundleDependencyDomainAndBarrierEntriesAreExact()
    {
        AssertSequence(
            new[]
            {
                "LABEL",
                "FIELD",
                "TABLE",
                "BASIC",
                "ADVANCED",
                "DIFF",
                "FLOW_CONTRIBUTION",
                "READINESS"
            },
            StatConfigBundleDependencyKinds.Ordered,
            "bundle dependency kinds");
        AssertSet(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "P9_RUN",
                "P9_PROJECTION",
                "P9_RESULT",
                "P9_EXPORT",
                "P10_RECONCILE"
            },
            StatConfigPhaseBarrierEntries.All,
            "phase barrier entries");
    }

    private static void EmptyBundleStatesAndHashAreDeterministicAndDistinct()
    {
        var service = new StatConfigBundleService(null!);
        var first = service.ReadEmpty(" unit ", OwnerId);
        var second = service.ReadEmpty("UNIT", OwnerId);

        Assert(first.IsEmpty, "empty bundle flag");
        Assert(first.Pins.Count == 0, "empty bundle pins");
        AssertEqual("EMPTY_VALID", first.Eligibility.Configuration,
            "empty configuration eligibility");
        AssertEqual("EMPTY_VALID", first.Eligibility.FutureResult,
            "empty future result readiness");
        AssertEqual("UNSUPPORTED", first.Eligibility.Executor,
            "unsupported executor state");
        AssertEqual("P9", first.Eligibility.TargetPhase,
            "executor target phase");
        AssertEqual("EMPTY_VALID", first.Freshness,
            "empty freshness state");
        AssertEqual(first.CanonicalJson, second.CanonicalJson,
            "deterministic empty canonical JSON");
        AssertEqual(first.BundleHash, second.BundleHash,
            "deterministic empty bundle hash");
        AssertEqual(
            StatConfigCanonicalJson.HashUtf8(first.CanonicalJson),
            first.BundleHash,
            "API readback hash recomputation");

        var other = service.ReadEmpty(
            "UNIT",
            "000000000000000000000902");
        Assert(first.BundleHash != other.BundleHash,
            "owner identity must contribute to bundle hash");
    }

    private static void BarrierProbeRequiresActorCommandAndHashShape()
    {
        StatConfigPhaseBarrier.ValidateProbeRequest(new(
            "unit",
            OwnerId,
            "p809-barrier-command",
            BundleHash));

        _ = AssertThrows(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () => StatConfigPhaseBarrier.ValidateProbeRequest(new(
                "UNIT",
                OwnerId,
                "bad command whitespace",
                BundleHash)));
        _ = AssertThrows(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () => StatConfigPhaseBarrier.ValidateProbeRequest(new(
                "UNIT",
                OwnerId,
                "p809-command",
                "not-a-hash")));
    }

    private static void EveryBarrierEntryReturnsTheStablePhaseContract()
    {
        foreach (var entry in StatConfigPhaseBarrierEntries.All)
        {
            var error = AssertThrows(
                AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
                () => StatConfigPhaseBarrier.Reject(entry));
            var details = error.Details?.ToString() ?? string.Empty;
            AssertContains(details, entry, "barrier entry");
            AssertContains(details, "P8_CONFIG_ONLY", "barrier reason");
            AssertContains(details, "BLOCKED_UNTIL_TARGET_PHASE",
                "barrier eligibility");
            AssertContains(
                details,
                entry == "P10_RECONCILE" ? "P10" : "P9",
                "barrier target phase");
        }
    }

    private static AppException AssertThrows(
        AppErrorCode code,
        Action action)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(code, error.Code, "error code");
            return error;
        }
        throw new InvalidOperationException($"Expected {code}.");
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string label)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException($"{label} differs.");
    }

    private static void AssertSet(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual,
        string label)
    {
        if (!expected.SetEquals(actual))
            throw new InvalidOperationException($"{label} differs.");
    }

    private static void AssertContains(
        string value,
        string expected,
        string label)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} missing {expected}.");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"{label} failed.");
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected {expected}, actual {actual}.");
        }
    }
}
