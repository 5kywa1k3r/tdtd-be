namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P809BundleRoute = "api/stat-config/bundle";
    private const string P809BarrierRoute = "api/stat-config/barriers";

    private static readonly string[] P809DependencyKinds =
    [
        "LABEL",
        "FIELD",
        "TABLE",
        "BASIC",
        "ADVANCED",
        "DIFF",
        "FLOW_CONTRIBUTION",
        "READINESS"
    ];

    private static readonly P809OwnedCaseContract[] P809OwnedCaseContracts =
    [
        new("P8-BND-001", "canonical full bundle pins all eight owner domains", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "HASH"]),
        new("P8-BND-002", "persisted owner view and API readback independently recompute the same semantic hash", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "HASH"]),
        new("P8-BND-003", "mixed or stale dependency pins reject without implicit owner/catalog upgrade", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "HASH"]),
        new("P8-BND-004", "valid empty bundle/readback is deterministic and distinct from result state", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "HASH"]),
        new("P8-BND-005", "P9 run entry is blocked before enqueue or write", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-006", "P9 projection and result entries are blocked before write", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-007", "P9 export entry is blocked before write", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-008", "P10 reconcile entry is blocked before write", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-009", "six field/table/label value and aggregate projections have exact zero delta", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-010", "Basic snapshot store has exact zero delta", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-011", "Advanced hierarchy and Diff output stores have exact zero delta", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-012", "report/projection/aggregate/export/reconcile/job/outbox stores have exact zero delta", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-BND-013", "empty valid readiness is explicit, eligible and fresh without claiming a result", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "HASH"]),
        new("P8-BND-014", "unsupported, stale, queued and empty future result states remain distinct", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "HASH"])
    ];

    private readonly List<P809BundleHashEvidence> _p809BundleHashes = [];
    private readonly List<P809BarrierEvidence> _p809Barriers = [];
    private readonly List<P809StateEvidence> _p809States = [];
    private readonly List<P809ActualRouteEvidence> _p809ActualRoutes = [];

    private async Task RunBundleAndPhaseBarrierCasesAsync(CancellationToken ct)
    {
        await WriteP809SourceFreezeAsync(ct);
        await WriteP809CaseContractAsync(ct);
        await WriteP809ZeroWriteInventoryAsync(ct);
        await AwaitP809InfrastructureBaselineAsync(ct);
        await SeedP809FixturesAsync(ct);
        await RunP809BundleCasesAsync(ct);
        await RunP809BarrierCasesAsync(ct);
        await RunP809ZeroDeltaCasesAsync(ct);
        await RunP809FreshnessCasesAsync(ct);
        await WriteP809BundleAndBarrierEvidenceAsync(ct);
    }
}

internal sealed record P809OwnedCaseContract(
    string CaseId,
    string Contract,
    IReadOnlyList<string> Oracles);

internal sealed record P809BundleHashEvidence(
    string CaseId,
    string BundleHash,
    string RecomputedHash,
    IReadOnlyList<string> DependencyKinds,
    int PinCount,
    string? OwnerDocumentSetSha256,
    string DirectMongoCanonicalJson,
    string DirectMongoBundleHash,
    bool DirectMongoCanonicalMatch,
    bool DirectMongoHashMatch);

internal sealed record P809BarrierEvidence(
    string CaseId,
    string Entry,
    string TargetPhase,
    int HttpStatus,
    string ErrorCode,
    string Reason,
    string Eligibility,
    string Freshness);

internal sealed record P809StateEvidence(
    string CaseId,
    string State,
    string Eligibility,
    string Freshness,
    bool IsComplete,
    string SemanticIdentity);

internal sealed record P809ActualRouteEvidence(
    string CaseId,
    string Entry,
    string Method,
    string Path,
    int HttpStatus,
    string ErrorCode,
    string? RequestedOperation,
    int ZeroWriteCollectionCount,
    string BeforeInventorySha256,
    string AfterInventorySha256);
