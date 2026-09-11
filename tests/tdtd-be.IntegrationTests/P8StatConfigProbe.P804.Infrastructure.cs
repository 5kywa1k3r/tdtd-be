using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] P804HangfireCollectionSuffixes =
    [
        ".jobGraph",
        ".locks",
        ".migrationLock",
        ".notifications",
        ".schema",
        ".server",
        ".stateHistory"
    ];

    private async Task AwaitP804InfrastructureBaselineAsync(
        CancellationToken ct)
    {
        var fixture = BasicFixture("infra");
        var (_, prewarm) = await PutBasicConfigAsync(
            Actor("system_admin"),
            fixture,
            "p8-bas-setup-infrastructure-baseline",
            BasicDirectPayload(
                Array.Empty<System.Text.Json.Nodes.JsonObject>()),
            ct);
        HarnessAssert.Equal(false, prewarm.IsVirtualEmpty,
            "P8-04 infrastructure prewarm did not persist config-only state");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        string[] matching = [];
        while (DateTime.UtcNow < deadline)
        {
            var names = await (await _database.ListCollectionNamesAsync(
                    cancellationToken: ct))
                .ToListAsync(ct);
            matching = names
                .Where(name => P804HangfireCollectionSuffixes.Any(
                    suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (P804HangfireCollectionSuffixes.All(suffix => matching.Any(
                    name => name.EndsWith(suffix, StringComparison.Ordinal))))
            {
                await EvidenceJson.WriteAsync(
                    Path.Combine(
                        _paths.RunRoot,
                        "p8-04-infrastructure-baseline.json"),
                    new
                    {
                        chainId = ChainId,
                        promptId = _promptId,
                        baselineEstablishedBeforeOwnedCaseSnapshots = true,
                        infrastructureCollections = matching,
                        prewarm = new
                        {
                            assignmentId = fixture.Assignment.Id,
                            commandId = "p8-bas-setup-infrastructure-baseline",
                            configId = prewarm.ConfigId,
                            versionId = prewarm.VersionId,
                            configHash = prewarm.ConfigHash,
                            fixtureWritesOutsideCaseDeltas = true
                        },
                        mutationAllowlistUnchanged = true
                    },
                    ct);
                return;
            }
            await Task.Delay(100, ct);
        }
        throw new HarnessCaseNotRunnableException(
            "P8-04 could not establish the isolated Hangfire infrastructure baseline before case snapshots; " +
            $"observed={string.Join(",", matching)}");
    }
}
