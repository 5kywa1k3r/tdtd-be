namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] P805HangfireInfrastructureSuffixes =
    [
        ".jobGraph",
        ".locks",
        ".migrationLock",
        ".notifications",
        ".schema",
        ".server",
        ".stateHistory"
    ];

    private async Task WarmP805InfrastructureAsync(CancellationToken ct)
    {
        var runRoot = _paths.RunRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var runKey = Path.GetFileName(runRoot);
        if (string.IsNullOrWhiteSpace(runKey))
        {
            throw new InvalidOperationException(
                "P8-05 infrastructure warmup cannot derive its run key.");
        }

        var runOwnedKey = new string(
                runKey.Where(char.IsLetterOrDigit).Take(24).ToArray())
            .ToLowerInvariant();
        if (runOwnedKey.Length == 0)
        {
            throw new InvalidOperationException(
                "P8-05 infrastructure warmup derived an empty run-owned key.");
        }

        var hangfirePrefix = $"p1hf_{runOwnedKey}";
        var expectedInfrastructureCollections =
            P805HangfireInfrastructureSuffixes
                .Select(suffix => hangfirePrefix + suffix)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        var expectedInfrastructureSet =
            expectedInfrastructureCollections.ToHashSet(
                StringComparer.Ordinal);

        var before = await CaptureDatabaseSnapshotAsync(ct);
        var exchangeStart = _api.Exchanges.Count;
        var fixture = AdvancedFixture("001");
        var warmed = await ReadAdvancedConfigAsync(
            Actor("system_admin"),
            fixture,
            ct,
            requirePersisted: false);
        var after = await CaptureDatabaseSnapshotAsync(ct);
        var deltas = BuildDeltas(before, after);
        var changed = deltas
            .Where(delta => delta.Changed)
            .ToArray();
        var unexpected = changed
            .Where(delta =>
                !expectedInfrastructureSet.Contains(delta.Collection))
            .ToArray();
        var contractChanges = changed
            .Where(delta => ContractCollections.Contains(
                delta.Collection,
                StringComparer.Ordinal))
            .ToArray();
        var prohibitedChanges = changed
            .Where(delta => ProhibitedCollections.Contains(
                delta.Collection,
                StringComparer.Ordinal))
            .ToArray();
        var missingInfrastructure = expectedInfrastructureCollections
            .Where(name =>
                !after.TryGetValue(name, out var state) || !state.Exists)
            .ToArray();

        await EvidenceJson.WriteAsync(
            Path.Combine(
                _paths.RunRoot,
                "p8-05-infrastructure-warmup.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                warmup = new
                {
                    readOnlyVirtualAdvancedGet = true,
                    fixtureKey = fixture.Key,
                    assignmentId = fixture.Assignment.Id,
                    warmed.IsVirtualEmpty,
                    warmed.Status,
                    warmed.Revision,
                    warmed.ConfigHash,
                    exchangeSequences = _api.Exchanges
                        .Skip(exchangeStart)
                        .Select(exchange => exchange.Sequence)
                        .ToArray()
                },
                hangfirePrefix,
                expectedInfrastructureCollections,
                before = before.Values
                    .OrderBy(state => state.Collection, StringComparer.Ordinal),
                after = after.Values
                    .OrderBy(state => state.Collection, StringComparer.Ordinal),
                deltas,
                assertions = new
                {
                    changedCollectionsAreExactRunOwnedInfrastructure =
                        unexpected.Length == 0,
                    allExpectedInfrastructurePresent =
                        missingInfrastructure.Length == 0,
                    contractCollectionDeltaZero = contractChanges.Length == 0,
                    prohibitedCollectionDeltaZero =
                        prohibitedChanges.Length == 0,
                    caseSnapshotCollectionContractRemainsStrict = true
                }
            },
            ct);

        VerifyCollectionContract(
            "P8-05/infrastructure-warmup",
            deltas,
            expectedInfrastructureCollections,
            Array.Empty<string>());
        HarnessAssert.True(missingInfrastructure.Length == 0,
            "P8-05 warmup did not initialize exact run-owned Hangfire collections: " +
            string.Join(", ", missingInfrastructure));
        HarnessAssert.True(contractChanges.Length == 0,
            "P8-05 warmup changed business contract collections: " +
            string.Join(", ", contractChanges.Select(item => item.Collection)));
        HarnessAssert.True(warmed.IsVirtualEmpty,
            "P8-05 warmup GET did not preserve virtual empty config state");
        HarnessAssert.Equal(0L, warmed.Revision,
            "P8-05 warmup GET changed virtual config revision");
        HarnessAssert.Equal(EmptyConfigHash, warmed.ConfigHash,
            "P8-05 warmup GET changed virtual config hash");
    }
}
