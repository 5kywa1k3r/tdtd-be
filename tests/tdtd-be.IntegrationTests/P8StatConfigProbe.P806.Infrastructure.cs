using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task AwaitP806InfrastructureBaselineAsync(CancellationToken ct)
    {
        var runRoot = _paths.RunRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var runKey = Path.GetFileName(runRoot);
        if (string.IsNullOrWhiteSpace(runKey))
        {
            throw new InvalidOperationException(
                "P8-06 infrastructure warmup cannot derive its run key.");
        }

        var runOwnedKey = new string(
                runKey.Where(char.IsLetterOrDigit).Take(24).ToArray())
            .ToLowerInvariant();
        if (runOwnedKey.Length == 0)
        {
            throw new InvalidOperationException(
                "P8-06 infrastructure warmup derived an empty run-owned key.");
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
        var fixture = DiffFixture("001");

        // The Diff service intentionally has no job client. Resolve the
        // Advanced config service through a virtual read on the same owner to
        // initialize Hangfire.Mongo without enqueuing or persisting config.
        var advancedFixture = new P8AdvancedFixture(
            "p806-infrastructure",
            fixture.DynamicFormTemplateId,
            "main",
            fixture.Assignment,
            "system_admin");
        var advancedWarm = await ReadAdvancedConfigAsync(
            Actor("system_admin"),
            advancedFixture,
            ct,
            requirePersisted: false);
        var diffWarm = await ReadDiffConfigAsync(
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
        var diffRows = await _database
            .GetCollection<BsonDocument>(DiffConfigsCollection)
            .CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);

        await EvidenceJson.WriteAsync(
            Path.Combine(
                _paths.RunRoot,
                "p8-06-infrastructure-baseline.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                baselineEstablishedBeforeOwnedCaseSnapshots = true,
                warmup = new
                {
                    advancedReadOnlyVirtualGet = true,
                    diffReadOnlyVirtualGet = true,
                    fixtureKey = fixture.Key,
                    assignmentId = fixture.Assignment.Id,
                    advancedIsVirtualEmpty = advancedWarm.IsVirtualEmpty,
                    advancedStatus = advancedWarm.Status,
                    advancedRevision = advancedWarm.Revision,
                    advancedConfigHash = advancedWarm.ConfigHash,
                    diffIsVirtualEmpty = diffWarm.IsVirtualEmpty,
                    diffStatus = diffWarm.Status,
                    diffRevision = diffWarm.Revision,
                    diffConfigHash = diffWarm.ConfigHash,
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
                diffConfigRows = diffRows,
                assertions = new
                {
                    changedCollectionsAreExactRunOwnedInfrastructure =
                        unexpected.Length == 0,
                    allExpectedInfrastructurePresent =
                        missingInfrastructure.Length == 0,
                    contractCollectionDeltaZero = contractChanges.Length == 0,
                    prohibitedCollectionDeltaZero =
                        prohibitedChanges.Length == 0,
                    mutationAllowlistUnchanged = true
                }
            },
            ct);

        VerifyCollectionContract(
            "P8-06/infrastructure-warmup",
            deltas,
            expectedInfrastructureCollections,
            Array.Empty<string>());
        HarnessAssert.True(missingInfrastructure.Length == 0,
            "P8-06 warmup did not initialize exact run-owned Hangfire collections: " +
            string.Join(", ", missingInfrastructure));
        HarnessAssert.True(contractChanges.Length == 0,
            "P8-06 warmup changed business contract collections: " +
            string.Join(", ", contractChanges.Select(item => item.Collection)));
        HarnessAssert.True(prohibitedChanges.Length == 0,
            "P8-06 warmup changed prohibited collections: " +
            string.Join(", ", prohibitedChanges.Select(item => item.Collection)));
        HarnessAssert.True(advancedWarm.IsVirtualEmpty,
            "P8-06 Advanced infrastructure warmup persisted config state");
        HarnessAssert.True(diffWarm.IsVirtualEmpty,
            "P8-06 Diff infrastructure warmup persisted config state");
        HarnessAssert.Equal(0L, diffRows,
            "P8-06 infrastructure baseline unexpectedly contains Diff configs");
    }
}
