using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private P8ConfigIdentity? _p808Owner;
    private readonly Dictionary<string, P8ConfigIdentity> _p808Owners =
        new(StringComparer.Ordinal);

    private async Task SeedP808OwnerFixtureAsync(CancellationToken ct)
    {
        var specs = new[]
        {
            (Key: "baseline", Suffix: "owner"),
            (Key: "retry", Suffix: "retry"),
            (Key: "lease", Suffix: "lease"),
            (Key: "heartbeat", Suffix: "heartbeat"),
            (Key: "timeout", Suffix: "timeout"),
            (Key: "reset", Suffix: "reset"),
            (Key: "race", Suffix: "race"),
            (Key: "quota-a", Suffix: "quota-a"),
            (Key: "quota-b", Suffix: "quota-b"),
            (Key: "cleanup", Suffix: "cleanup")
        };
        var seeded = new List<object>(specs.Length);
        foreach (var spec in specs)
        {
            var commandId = $"p808-fixture-label-{spec.Key}";
            var (_, owner) = await CreateLabelAsync(
                Actor("system_admin"),
                commandId,
                LabelPayload(
                    $"p8.ops.readiness.{spec.Suffix}",
                    $"P8 Operations Readiness {spec.Key}",
                    "GLOBAL",
                    null,
                    usage: "STATISTIC",
                    dataType: "NUMBER"),
                ct);
            _p808Owners.Add(spec.Key, owner);
            seeded.Add(new
            {
                spec.Key,
                owner.OwnerKind,
                owner.OwnerId,
                owner.ConfigId,
                owner.VersionId,
                owner.VersionNo,
                owner.Revision,
                owner.Status,
                owner.ConfigHash,
                owner.DependencyPins,
                commandId,
                owner.ReceiptId
            });
        }
        _p808Owner = _p808Owners["baseline"];

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ops-owner-fixture.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                seededThroughRealKestrel = true,
                isolatedOwnerCount = seeded.Count,
                owners = seeded
            },
            ct);
    }

    private P8ConfigIdentity RequireP808Owner()
        => _p808Owner ?? throw new HarnessCaseNotRunnableException(
            "P8-08 config-owner fixture is unavailable.");

    private P8ConfigIdentity RequireP808Owner(string key)
        => _p808Owners.TryGetValue(key, out var owner)
            ? owner
            : throw new HarnessCaseNotRunnableException(
                $"P8-08 config-owner fixture '{key}' is unavailable.");

    private static JsonObject P808EnqueueEnvelope(
        string commandId,
        P8ConfigIdentity owner,
        string? configId = null,
        string? versionId = null,
        int? versionNo = null,
        long? expectedRevision = null,
        string? expectedConfigHash = null)
        => Envelope(
            commandId,
            expectedRevision ?? owner.Revision,
            expectedConfigHash ?? owner.ConfigHash,
            new JsonObject
            {
                ["configId"] = configId ?? owner.ConfigId,
                ["versionId"] = versionId ?? owner.VersionId,
                ["versionNo"] = versionNo ?? owner.VersionNo
            });

    private static JsonObject P808StateEnvelope(
        string commandId,
        long stateRevision,
        string stateHash,
        JsonObject payload)
        => Envelope(commandId, stateRevision, stateHash, payload);
}

