namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P808JobsCollection = "stat_config_validation_jobs";
    private const string P808AuditOutboxCollection = "stat_config_audit_outbox";
    private const string P808QueueName = "stat-config-readiness";
    private const string P808PublicJobsRoute = "api/stat-config/readiness/jobs";
    private const string P808AdminJobsRoute =
        "api/admin/operations/job-runs/stat-config-readiness-jobs";

    private readonly List<P808QueueTrace> _p808QueueTraces = [];
    private readonly List<P808IndexQueryEvidence> _p808IndexQueries = [];
    private readonly List<P808DiagnosticScanEvidence> _p808DiagnosticScans = [];

    private static readonly P808OwnedCaseContract[] P808OwnedCaseContracts =
    [
        new("P8-OPS-001", "atomic enqueue + audit-outbox commit", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "QUEUE_TRACE"]),
        new("P8-OPS-002", "fault between job and audit-outbox rolls back all writes", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-OPS-003", "real PENDING/RUNNING/COMPLETED maps to QUEUED/RUNNING/DONE", ["API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE"]),
        new("P8-OPS-004", "exact enqueue replay dedupes to the original job", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-OPS-005", "stale or divergent config hash pin rejects with zero writes", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-OPS-006", "transient worker failure schedules bounded exponential retry", ["API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE"]),
        new("P8-OPS-007", "worker restart reclaims an expired lease exactly once", ["API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE"]),
        new("P8-OPS-008", "heartbeat preserves one active lease owner and blocks a rival", ["API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE"]),
        new("P8-OPS-009", "timeout exhausts bounded attempts into FAILED/dead-letter", ["API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE"]),
        new("P8-OPS-010", "admin reset is quota-neutral and the reset job can complete", ["API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE"]),
        new("P8-OPS-011", "startup owns the exact statistics/readiness index inventory once", ["DIRECT_MONGO", "INDEX_QUERY"]),
        new("P8-OPS-012", "dedupe/correlation unique partial indexes enforce exact keys", ["DIRECT_MONGO", "INDEX_QUERY"]),
        new("P8-OPS-013", "atomic-claim query shape uses the dedicated claim index", ["DIRECT_MONGO", "INDEX_QUERY"]),
        new("P8-OPS-014", "same-key wrong-option index makes startup fail closed without drop/rebuild", ["API_KESTREL", "DIRECT_MONGO", "INDEX_QUERY"]),
        new("P8-OPS-015", "ordinary actor reads only business-safe readiness status", ["API_KESTREL", "DIRECT_MONGO", "SECURITY_SCAN"]),
        new("P8-OPS-016", "ordinary actor diagnostics authorization precedes existence", ["API_KESTREL", "DIRECT_MONGO", "SECURITY_SCAN"]),
        new("P8-OPS-017", "admin diagnostics retain correlation but redact sensitive internals", ["API_KESTREL", "DIRECT_MONGO", "SECURITY_SCAN"]),
        new("P8-OPS-018", "quota rejection happens before job/outbox/receipt/result writes", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-OPS-019", "concurrent duplicate enqueue has one durable winner", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "QUEUE_TRACE"]),
        new("P8-OPS-020", "cleanup retry touches only expired owned rows and is idempotent", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "QUEUE_TRACE"])
    ];

    private async Task RunOperationsReadinessCasesAsync(CancellationToken ct)
    {
        await WriteP808SourceFreezeAsync(ct);
        await WriteP808CaseContractAsync(ct);
        await CaptureP808StatisticsIndexInventoryAsync(ct);
        await SeedP808OwnerFixtureAsync(ct);
        await RunP808StateAndDedupeCasesAsync(ct);
        await RunP808RetryLeaseAndDeadLetterCasesAsync(ct);
        await RunP808IndexCasesAsync(ct);
        await RunP808DiagnosticCasesAsync(ct);
        await RunP808QuotaRaceAndCleanupCasesAsync(ct);
        await WriteP808OperationsEvidenceAsync(ct);
    }
}

internal sealed record P808OwnedCaseContract(
    string CaseId,
    string Contract,
    IReadOnlyList<string> Oracles);

