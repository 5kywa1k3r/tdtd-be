namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly P810OwnedCaseContract[] P810OwnedCaseContracts =
    [
        new("P8-UI-001", "canonical /works/:workId/statistics/:scopeAssignmentId/config/:tab? route owns work, scope, tab and deep-link identity", ["API_KESTREL", "DIRECT_MONGO", "ROUTE_BINDING"]),
        new("P8-UI-002", "form-statistics editor has one canonical owner and no competing editor", ["API_KESTREL", "DIRECT_MONGO", "ROUTE_BINDING"]),
        new("P8-UI-003", "flow contribution and operations readiness routes remain separate from configuration, result and reconcile surfaces", ["API_KESTREL", "ROUTE_BINDING"]),
        new("P8-UI-004", "OWNER, ISSUER, REPORTER, REVIEWER, COORDINATOR, SYSTEM_ADMIN and OUTSIDER have an exact fail-closed guard matrix", ["API_KESTREL", "DIRECT_MONGO", "ACTOR_MATRIX"]),
        new("P8-UI-005", "classification-label and statistic-field label layers remain distinct", ["API_KESTREL", "DIRECT_MONGO"]),
        new("P8-UI-006", "table metric and row-label identities remain distinct", ["API_KESTREL", "DIRECT_MONGO"]),
        new("P8-UI-007", "typed field and table methods/scopes expose only compatible configuration", ["API_KESTREL", "DIRECT_MONGO"]),
        new("P8-UI-008", "form configuration identity, version, hash, readback and explicit UI states are stable", ["API_KESTREL", "DIRECT_MONGO", "HASH"]),
        new("P8-UI-009", "Basic typed edit uses an exact command, optimistic CAS, receipt and readback", ["API_KESTREL", "DIRECT_MONGO", "CAS", "RECEIPT"]),
        new("P8-UI-010", "Basic version, lock, hash, readback and stale-conflict behavior are stable", ["API_KESTREL", "DIRECT_MONGO", "CAS", "RECEIPT"]),
        new("P8-UI-011", "Advanced draft, section gates, budget and quota use an exact command and CAS", ["API_KESTREL", "DIRECT_MONGO", "CAS", "RECEIPT"]),
        new("P8-UI-012", "Advanced lock, version, hash, readback, valid-empty and hierarchy remain configuration-only", ["API_KESTREL", "DIRECT_MONGO", "CAS", "RECEIPT"]),
        new("P8-UI-013", "Diff concept, period, direction, scope and missing-value policy use an exact command and CAS", ["API_KESTREL", "DIRECT_MONGO", "CAS", "RECEIPT"]),
        new("P8-UI-014", "Diff lock, version, hash, readback and stale conflict produce no result delta", ["API_KESTREL", "DIRECT_MONGO", "CAS", "RECEIPT"]),
        new("P8-UI-015", "readiness validation and enqueue states distinguish loading, empty, retrying, success and unsupported", ["API_KESTREL", "DIRECT_MONGO"]),
        new("P8-UI-016", "business readiness permission and redacted administrator diagnostics are separated", ["API_KESTREL", "DIRECT_MONGO", "ACTOR_MATRIX"]),
        new("P8-UI-017", "no-dataset lifecycle, retry and reset remain explicit with zero result writes", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-UI-018", "Flow statistic contribution defaults to EXCLUDE; INCLUDE is explicit, authorized and warned", ["API_KESTREL", "DIRECT_MONGO"]),
        new("P8-UI-019", "non-empty Flow statisticProfile exposes FLOW_STATISTIC_PROFILE_DISABLED and rejects without writes", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-UI-020", "Run, Export and Reconcile actions are hidden and uncallable behind the P9/P10 barrier with exact 53-store zero delta", ["API_KESTREL", "DIRECT_MONGO", "ROUTE_BINDING", "COLLECTION_DELTA"])
    ];

    private async Task RunStatisticsConfigurationUiCasesAsync(
        string[] args,
        CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-case-contract.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                exactCaseCount = P810OwnedCaseContracts.Length,
                exactCaseIds = P810OwnedCaseContracts.Select(item => item.CaseId).ToArray(),
                contracts = P810OwnedCaseContracts
            },
            ct);

        var externalOracles = await LoadP810ExternalOracleInputsAsync(args, ct);
        await AwaitP809InfrastructureBaselineAsync(ct);
        await SeedP809FixturesAsync(ct);
        await BindP810ActorRelationshipsAsync(ct);
        await BuildP810FixturesAsync(ct);
        await SeedP810BlockedProfileFixtureAsync(ct);
        await WriteP810FixtureAndActorEvidenceAsync(externalOracles, ct);

        await RunP810RouteAndGuardCasesAsync(ct);
        await RunP810LabelAndFormCasesAsync(ct);
        await RunP810ConfigurationCasesAsync(ct);
        await RunP810ReadinessAndFlowCasesAsync(ct);

        await WriteP810BindingsAsync(externalOracles, ct);
    }
}

internal sealed record P810OwnedCaseContract(
    string CaseId,
    string Contract,
    IReadOnlyList<string> Oracles);
