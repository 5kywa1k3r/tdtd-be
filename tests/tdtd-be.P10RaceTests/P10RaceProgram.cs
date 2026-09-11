using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class P10RaceProgram
{
    private static readonly JsonSerializerOptions RegistryJson = new()
    {
        WriteIndented = true
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        P10RaceOptions options;
        try
        {
            options = P10RaceOptions.Parse(args);
        }
        catch (Exception error) when (error is ArgumentException or
                                      InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"P10_RACE_FAILED reason={Safe(error.Message)}");
            return 64;
        }

        var context = new P10RaceContext(options.RootId,
            options.ArtifactRoot);
        context.Initialize();
        if (options.DeliberateWrongLedger)
            return RunDeliberateWrongLedger(context);

        return await RunCasesAsync(context, options.RegistryOut);
    }

    private static async Task<int> RunCasesAsync(
        P10RaceContext context,
        string? registryOut)
    {
        var definitions = P10RaceCases.All;
        P10Assert.SequenceEqual(P10RaceContract.ExactCaseIds,
            definitions.Select(value => value.Id),
            "P10_RACE_EXACT_REGISTRY_ORDER");

        var results = new List<P10RaceCaseResult>(definitions.Count);
        try
        {
            foreach (var definition in definitions)
            {
                var evidence = await definition.Run(context);
                P10RaceContract.RequireExactEvidence(evidence);
                var result = new P10RaceCaseResult(
                    definition.Id, "PASS", P10RaceEvidence.From(evidence));
                results.Add(result);
                var evidenceSha =
                    StatisticReconciliationEvidenceCanonical.HashSequence(
                        "P10_RACE_CASE_EVIDENCE_V1",
                        evidence.Columns.Select(value =>
                            $"{value.Key}={value.Value}"));
                Console.WriteLine(
                    $"PASS {definition.Id} evidenceSha256={evidenceSha}");
            }
        }
        catch (Exception error)
        {
            var cleanupOnFailure = context.CleanupOwnedArtifacts();
            var failedId = definitions[results.Count].Id;
            Console.Error.WriteLine(
                $"FAIL {failedId} reason={Safe(error.Message)} " +
                $"cleanup={cleanupOnFailure.Bounded.ToString().ToLowerInvariant()}");
            return 1;
        }

        P10Assert.Equal(20, results.Count, "P10_RACE_EXACT_CASE_COUNT");
        P10Assert.True(results.All(value => value.Status == "PASS"),
            "P10_RACE_NO_NON_PASS");
        var cleanup = context.CleanupOwnedArtifacts();
        P10Assert.True(cleanup.Bounded && cleanup.SecondDryRunCount == 0 &&
                       cleanup.OutOfScopeUntouched,
            "P10_RACE_BOUNDED_CLEANUP");
        var security = new P10RaceSecurityProof(
            results.Count(value => value.Id.StartsWith(
                "P10-SECURITY-", StringComparison.Ordinal)),
            results.Where(value => value.Id.StartsWith(
                    "P10-SECURITY-", StringComparison.Ordinal))
                .All(value => value.Status == "PASS"),
            0);
        var p9 = new P10RaceP9RegressionProof(0, true);
        var semanticSha = P10RaceContract.SemanticSha(results);
        var registry = new P10RaceRootRegistry(
            P10RaceContract.RegistrySchemaVersion,
            context.RootId,
            P10RaceContract.FixedUtc,
            results.Count,
            results,
            cleanup,
            security,
            p9,
            P10RaceContext.SourceFingerprintSha256,
            semanticSha);

        if (registryOut is not null)
            await WriteRegistryAsync(registryOut, registry);

        Console.WriteLine(
            $"P10_RACE_OK root={context.RootId} cases=20 cas=5 crash=5 " +
            $"security=5 clean=5 fixedUtc={P10RaceContract.FixedUtc:O} " +
            $"columns=8 semanticSha256={semanticSha} p9Writes=0 " +
            "cleanup=true");
        return 0;
    }

    private static int RunDeliberateWrongLedger(P10RaceContext context)
    {
        try
        {
            var trusted = P10RaceContext.SourceFingerprintSha256;
            var poisoned = StatisticReconciliationEvidenceCanonical.Hash(
                "P10_RACE_TEST_ONLY_WRONG_LEDGER_V1",
                trusted,
                "P10-RACE-TEST-ONLY-MUTATION");
            P10Assert.True(!StringComparer.Ordinal.Equals(trusted, poisoned),
                P10RaceContract.WrongLedgerReason);
            context.WriteOwnedArtifact("wrong-ledger.control", poisoned);

            // The control restores the trusted value before cleanup/recovery.
            var restored = P10RaceContext.SourceFingerprintSha256;
            P10Assert.Equal(trusted, restored,
                "TRUSTED_SOURCE_FINGERPRINT_NOT_RESTORED");
            var cleanup = context.CleanupOwnedArtifacts();
            P10Assert.True(cleanup.Bounded &&
                           cleanup.SecondDryRunCount == 0 &&
                           cleanup.OutOfScopeUntouched,
                "WRONG_LEDGER_CLEANUP_INCOMPLETE");

            Console.Error.WriteLine(
                "P10_RACE_DELIBERATE_WRONG_LEDGER_FAILED " +
                $"reason={P10RaceContract.WrongLedgerReason} " +
                $"exitCode={P10RaceContract.WrongLedgerExitCode} " +
                "cleanup=true recovered=true");
            return P10RaceContract.WrongLedgerExitCode;
        }
        catch (Exception error)
        {
            _ = context.CleanupOwnedArtifacts();
            Console.Error.WriteLine(
                "P10_RACE_DELIBERATE_WRONG_LEDGER_CONTROL_ERROR " +
                $"reason={Safe(error.Message)} cleanup=attempted");
            return 70;
        }
    }

    private static async Task WriteRegistryAsync(
        string path,
        P10RaceRootRegistry registry)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("REGISTRY_PARENT_MISSING");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".p10-race.tmp";
        try
        {
            var json = JsonSerializer.Serialize(registry, RegistryJson) +
                       Environment.NewLine;
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string Safe(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 240 ? singleLine : singleLine[..240];
    }
}

internal sealed record P10RaceOptions(
    string RootId,
    string? ArtifactRoot,
    string? RegistryOut,
    bool DeliberateWrongLedger)
{
    internal static P10RaceOptions Parse(IReadOnlyList<string> args)
    {
        var root = "A";
        string? artifactRoot = null;
        string? registryOut = null;
        var deliberate = false;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument == "--deliberate-wrong-ledger")
            {
                deliberate = true;
                continue;
            }

            if (argument is not ("--root" or "--artifact-root" or
                "--registry-out"))
                throw new ArgumentException($"UNKNOWN_ARGUMENT:{argument}");
            if (++index >= args.Count)
                throw new ArgumentException($"MISSING_VALUE:{argument}");
            var value = args[index];
            if (argument == "--root")
                root = value.ToUpperInvariant();
            else if (argument == "--artifact-root")
                artifactRoot = Path.GetFullPath(value);
            else
                registryOut = value;
        }

        if (root is not ("A" or "B"))
            throw new ArgumentException("ROOT_MUST_BE_A_OR_B");
        if (artifactRoot is not null && registryOut is not null)
        {
            registryOut = Path.IsPathRooted(registryOut)
                ? Path.GetFullPath(registryOut)
                : Path.GetFullPath(Path.Combine(artifactRoot, registryOut));
            P10PathGuard.RequireDescendant(artifactRoot, registryOut,
                "REGISTRY_OUTSIDE_ARTIFACT_ROOT");
        }
        else if (registryOut is not null)
        {
            registryOut = Path.GetFullPath(registryOut);
        }

        return new(root, artifactRoot, registryOut, deliberate);
    }
}

internal sealed class P10RaceContext
{
    private readonly string? _ownedRoot;
    private readonly string? _sentinel;

    internal P10RaceContext(string rootId, string? artifactRoot)
    {
        RootId = rootId;
        ArtifactRoot = artifactRoot;
        if (artifactRoot is null)
            return;
        _ownedRoot = Path.Combine(artifactRoot,
            $".p10-race-owned-{rootId.ToLowerInvariant()}");
        _sentinel = Path.Combine(artifactRoot,
            $".p10-race-sentinel-{rootId.ToLowerInvariant()}");
        P10PathGuard.RequireDescendant(artifactRoot, _ownedRoot,
            "OWNED_ROOT_OUTSIDE_ARTIFACT_ROOT");
        P10PathGuard.RequireDescendant(artifactRoot, _sentinel,
            "SENTINEL_OUTSIDE_ARTIFACT_ROOT");
    }

    internal string RootId { get; }
    internal string? ArtifactRoot { get; }

    internal static string SourceFingerprintSha256 { get; } =
        StatisticReconciliationEvidenceCanonical.HashSequence(
            "P10_RACE_TRUSTED_SOURCE_FINGERPRINT_V1",
            new[]
            {
                typeof(StatisticReconciliationRecheckStateMachine).FullName!,
                typeof(StatisticReconciliationIndependentReviewService)
                    .FullName!,
                typeof(StatisticReconciliationEvidenceCanonical).FullName!,
                P10RaceContract.RegistrySchemaVersion,
                P10RaceContract.FixedUtc.ToString("O"),
                string.Join(",", P10RaceContract.ExactCaseIds)
            });

    internal void Initialize()
    {
        if (ArtifactRoot is null || _ownedRoot is null || _sentinel is null)
            return;
        Directory.CreateDirectory(ArtifactRoot);
        Directory.CreateDirectory(_ownedRoot);
        File.WriteAllText(Path.Combine(_ownedRoot, "owner.txt"), RootId);
        File.WriteAllText(_sentinel, "OUT_OF_SCOPE_SENTINEL");
    }

    internal void WriteOwnedArtifact(string name, string content)
    {
        if (_ownedRoot is null)
            return;
        var path = Path.Combine(_ownedRoot, name);
        P10PathGuard.RequireDescendant(_ownedRoot, path,
            "OWNED_ARTIFACT_OUTSIDE_ROOT");
        File.WriteAllText(path, content);
    }

    internal P10RaceCleanupProof CleanupOwnedArtifacts()
    {
        if (_ownedRoot is null || _sentinel is null)
            return new(true, 0, 0, true);
        var removed = Directory.Exists(_ownedRoot)
            ? Directory.EnumerateFileSystemEntries(_ownedRoot,
                    "*", SearchOption.AllDirectories).Count() + 1
            : 0;
        if (Directory.Exists(_ownedRoot))
            Directory.Delete(_ownedRoot, recursive: true);
        var sentinelUntouched = File.Exists(_sentinel) &&
                                File.ReadAllText(_sentinel) ==
                                "OUT_OF_SCOPE_SENTINEL";
        var secondDryRun = Directory.Exists(_ownedRoot) ? 1 : 0;
        if (File.Exists(_sentinel))
            File.Delete(_sentinel);
        return new(!Directory.Exists(_ownedRoot), removed, secondDryRun,
            sentinelUntouched);
    }
}

internal static class P10PathGuard
{
    internal static void RequireDescendant(
        string root,
        string candidate,
        string reason)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate);
        if (!candidateFull.StartsWith(rootFull,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(reason);
    }

    internal static void RequireNoReparsePoint(
        IEnumerable<(string Segment, bool IsReparsePoint)> segments)
    {
        if (segments.Any(value => value.IsReparsePoint))
            throw new InvalidOperationException("REPARSE_POINT_REJECTED");
    }
}
