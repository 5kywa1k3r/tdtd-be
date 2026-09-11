using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P811FaultCommand = "p808-fault-p811-transition-007";
    private const string P811CreateReplayCommand = "p811-create-replay-001";

    private static readonly P811OwnedCaseContract[] P811OwnedCaseContracts =
    [
        new("P8-RACE-001", "barrier-released same-command create replay has one label/receipt and one canonical response", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT"]),
        new("P8-RACE-002", "barrier-released same-command Basic edit replay advances once", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT"]),
        new("P8-RACE-003", "barrier-released same-command Basic lock replay locks once", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT"]),
        new("P8-RACE-004", "divergent replay and stale hash both fail closed", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT"]),
        new("P8-RACE-005", "concurrent edit-vs-lock has one CAS winner and stable zero-write loser", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT"]),
        new("P8-RACE-006", "lock-vs-next-version has one immutable lock winner", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT"]),
        new("P8-RACE-007", "before/after job-write faults roll back owner/receipt/outbox", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-RACE-008", "before/after receipt-write faults roll back owner/receipt/outbox", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-RACE-009", "before/after audit-outbox faults roll back owner/receipt/outbox", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA"]),
        new("P8-RACE-010", "post-fault duplicate enqueue converges on one job/receipt/outbox", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT", "QUEUE_TRACE"]),
        new("P8-RACE-011", "a replacement Kestrel worker reclaims the persisted job after process restart", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "QUEUE_TRACE", "SOURCE_FINGERPRINT"]),
        new("P8-RACE-012", "future heartbeat lease excludes concurrent rival workers", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "QUEUE_TRACE"]),
        new("P8-RACE-013", "expired lease admits exactly one worker and never double-completes", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "QUEUE_TRACE"]),
        new("P8-RACE-014", "cleanup transient rollback, retry ownership and replay are exact", ["API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA", "RECEIPT", "QUEUE_TRACE"]),
        new("P8-RACE-015", "backend/integration/frontend source and assembly fingerprints are complete", ["API_KESTREL", "DIRECT_MONGO", "SOURCE_FINGERPRINT"]),
        new("P8-RACE-016", "security hygiene and immutable P7 successor-stale regression are truthful", ["API_KESTREL", "DIRECT_MONGO", "SECURITY_SCAN", "P7_REGRESSION"])
    ];

    private readonly Dictionary<string, P8BasicFixture> _p811BasicFixtures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, P8ConfigIdentity> _p811ReadinessOwners =
        new(StringComparer.Ordinal);
    private P8ConfigIdentity? _p811CreateReplayIdentity;
    private string? _p811FaultJobId;
    private string? _p811LeaseJobId;
    private ApiHarnessClient? _p811InitialApi;
    private BackendServerLease? _p811InitialBackend;
    private ApiHarnessClient? _p811RestartApi;
    private BackendServerLease? _p811RestartBackend;
    private readonly List<ApiExchangeEvidence> _p811RestartApiExchanges = [];
    private int _apiExchangeSequenceOffset;
    private P811BackendRestartEvidence? _p811BackendRestart;

    private async Task RunP811RaceCasesAsync(CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-race-case-contract.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = _promptId,
                requirementOwned = "P8-STAT-024",
                exactCaseCount = P811OwnedCaseContracts.Length,
                exactCaseIds = P811OwnedCaseContracts.Select(item =>
                    item.CaseId),
                contracts = P811OwnedCaseContracts,
                requiredGroupOracles = new[]
                {
                    "API_KESTREL", "DIRECT_MONGO", "COLLECTION_DELTA",
                    "RECEIPT", "QUEUE_TRACE", "SOURCE_FINGERPRINT",
                    "SECURITY_SCAN", "P7_REGRESSION"
                }
            },
            ct);
        await DrainP811PreexistingActiveJobsAsync(ct);
        await SeedP811RaceFixturesAsync(ct);

        await RunP811CreateReplayRaceAsync(ct);
        await RunP811BasicEditReplayRaceAsync(ct);
        await RunP811BasicLockReplayRaceAsync(ct);
        await RunP811DivergentAndStaleRaceAsync(ct);
        await RunP811EditVsLockRaceAsync(ct);
        await RunP811LockVsNextDraftRaceAsync(ct);
        await RunP811FaultBoundaryPairAsync(
            "P8-RACE-007", 1, "job", ct);
        await RunP811FaultBoundaryPairAsync(
            "P8-RACE-008", 3, "receipt", ct);
        await RunP811FaultBoundaryPairAsync(
            "P8-RACE-009", 5, "audit-outbox", ct);
        await RunP811DuplicateEnqueueAfterFaultsAsync(ct);

        try
        {
            await RestartP811BackendAsync(ct);
            await RunP811WorkerRestartRecoveryAsync(ct);
            await RunP811LiveLeaseExclusionAsync(ct);
            await RunP811ExpiredLeaseWinnerAsync(ct);
            await RunP811CleanupRetryAsync(ct);
            await RunP811SourceFingerprintAsync(ct);
            await RunP811SecurityAndP7RegressionAsync(ct);
        }
        finally
        {
            await StopP811RestartBackendAsync();
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-race-backend-restart.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = _promptId,
                restart = _p811BackendRestart,
                replacementApiExchanges = _p811RestartApiExchanges,
                exactOwnedCleanup =
                    _p811BackendRestart is { StopVerified: true, PortReleaseVerified: true }
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-race-queue-trace.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = _promptId,
                queue = P808QueueName,
                noDatasetWorker = true,
                traces = _p808QueueTraces.Where(item =>
                    item.CaseId.StartsWith("P8-RACE-", StringComparison.Ordinal))
            },
            ct);
    }

    private async Task SeedP811RaceFixturesAsync(CancellationToken ct)
    {
        var actor = Actor("system_admin");
        var baseFixture = BasicFixture("001");
        var form = await _database
            .GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .Find(item => item.Id == baseFixture.DynamicFormTemplateId &&
                          !item.IsDeleted)
            .SingleAsync(ct);
        var specs = new[]
        {
            "edit-replay", "lock-replay", "edit-lock", "lock-next"
        };
        var assignments = specs.Select((key, index) =>
                BuildBasicAssignmentFixture($"race-{key}", 200 + index, form))
            .ToArray();
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertManyAsync(
                assignments.Select(item => item.Assignment),
                cancellationToken: ct);
        foreach (var fixture in assignments)
            _p811BasicFixtures.Add(fixture.Key[5..], fixture);

        var draftPayload = P811BasicPayload("fixture-draft");
        foreach (var key in new[] { "lock-replay", "edit-lock", "lock-next" })
        {
            await PutBasicConfigAsync(
                actor,
                P811Basic(key),
                $"p811-fixture-{key}",
                draftPayload,
                ct);
        }

        foreach (var key in new[] { "fault", "lease" })
        {
            var (_, owner) = await CreateLabelAsync(
                actor,
                $"p811-fixture-readiness-{key}",
                LabelPayload(
                    $"p8.race.readiness.{key}",
                    $"P8 race readiness {key}",
                    "GLOBAL",
                    null,
                    usage: "STATISTIC",
                    dataType: "NUMBER"),
                ct);
            _p811ReadinessOwners.Add(key, owner);
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-race-fixtures.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = _promptId,
                seededThroughRealKestrel = true,
                fixtureWritesOutsideOwnedCaseDeltas = true,
                basicOwners = _p811BasicFixtures.Select(pair => new
                {
                    pair.Key,
                    assignmentId = pair.Value.Assignment.Id,
                    pair.Value.DynamicFormTemplateId
                }),
                readinessOwners = _p811ReadinessOwners.Select(pair => new
                {
                    pair.Key,
                    pair.Value.OwnerKind,
                    pair.Value.OwnerId,
                    pair.Value.ConfigId,
                    pair.Value.VersionId,
                    pair.Value.Revision,
                    pair.Value.ConfigHash
                })
            },
            ct);
    }

    private P8BasicFixture P811Basic(string key)
        => _p811BasicFixtures.TryGetValue(key, out var fixture)
            ? fixture
            : throw new HarnessCaseNotRunnableException(
                $"P8-11 Basic race fixture '{key}' is unavailable.");

    private P8ConfigIdentity P811ReadinessOwner(string key)
        => _p811ReadinessOwners.TryGetValue(key, out var owner)
            ? owner
            : throw new HarnessCaseNotRunnableException(
                $"P8-11 readiness owner '{key}' is unavailable.");

    private static System.Text.Json.Nodes.JsonObject P811BasicPayload(
        string semanticVariant)
    {
        var maxTextChars = semanticVariant switch
        {
            "fixture-draft" => 12000,
            "edit-replay" => 11000,
            "stale-hash" => 10000,
            "edit-winner" => 9000,
            _ => throw new ArgumentOutOfRangeException(
                nameof(semanticVariant),
                semanticVariant,
                "Unknown P8-11 Basic payload variant.")
        };
        return BasicDirectPayload(
            [BasicTarget("FIELD", "basic_number_sum", "NUMBER", "SUM")],
            detailHints: BasicDetailHints(maxTextChars: maxTextChars));
    }

    private async Task RestartP811BackendAsync(CancellationToken ct)
    {
        _p811InitialApi = _api;
        _p811InitialBackend = _backend;
        var actor = Actor("system_admin");
        var actorPassword = _bootstrapDefaultPassword;
        HarnessAssert.True(
            !string.IsNullOrWhiteSpace(actorPassword),
            "P8-11 bootstrap admin password is unavailable for restart.");
        var previousProcessId = _backend.ProcessId;
        var previousPort = _backend.Port;
        var restartStartedAtUtc = DateTime.UtcNow;
        await _backend.StopAsync();
        HarnessAssert.True(
            _backend.StopVerified && _backend.PortReleaseVerified,
            "P8-11 original Kestrel did not stop cleanly before restart.");

        var restartRoot = Path.Combine(_iterationRoot,
            "p811-worker-restart");
        Directory.CreateDirectory(restartRoot);
        _p811RestartBackend = await BackendServerLease.StartAsync(
            _paths,
            restartRoot,
            Path.GetFileName(_paths.RunRoot),
            _mongo,
            ct,
            BuildP811RestartBackendOptions());
        _p811RestartApi = new ApiHarnessClient(
            _p811RestartBackend.BaseUri);
        var token = await _p811RestartApi.LoginAsync(
            actor.Username,
            actorPassword,
            ct);
        RememberArtifactSecret("p811-restart-access-token", token);
        _actors[actor.Key] = actor with { Token = token };
        _apiExchangeSequenceOffset = _p811InitialApi.Exchanges.Count;
        _api = _p811RestartApi;
        _backend = _p811RestartBackend;
        _p811BackendRestart = new P811BackendRestartEvidence(
            restartStartedAtUtc,
            previousProcessId,
            previousPort,
            _p811InitialBackend.StopVerified,
            _p811InitialBackend.PortReleaseVerified,
            _p811RestartBackend.ProcessId,
            _p811RestartBackend.Port,
            _mongo.DatabaseName,
            P808QueueName,
            ReplacementAuthenticated: true,
            StopVerified: false,
            PortReleaseVerified: false,
            CompletedAtUtc: null);
    }

    private async Task StopP811RestartBackendAsync()
    {
        if (_p811RestartApi is not null)
        {
            _p811RestartApiExchanges.Clear();
            _p811RestartApiExchanges.AddRange(
                _p811RestartApi.Exchanges.Select(exchange =>
                    exchange with
                    {
                        Sequence = exchange.Sequence +
                                   _apiExchangeSequenceOffset
                    }));
            _p811RestartApi.Dispose();
        }
        if (_p811RestartBackend is not null)
        {
            await _p811RestartBackend.StopAsync();
            await _p811RestartBackend.DisposeAsync();
            if (_p811BackendRestart is not null)
            {
                _p811BackendRestart = _p811BackendRestart with
                {
                    StopVerified = _p811RestartBackend.StopVerified,
                    PortReleaseVerified =
                        _p811RestartBackend.PortReleaseVerified,
                    CompletedAtUtc = DateTime.UtcNow
                };
            }
        }
        if (_p811InitialApi is not null)
            _api = _p811InitialApi;
        if (_p811InitialBackend is not null)
            _backend = _p811InitialBackend;
        if (_p811RestartBackend is not null)
        {
            HarnessAssert.True(
                _p811RestartBackend.StopVerified &&
                _p811RestartBackend.PortReleaseVerified,
                "P8-11 replacement Kestrel cleanup was not verified.");
        }
    }

    private static BackendServerOptions BuildP811RestartBackendOptions()
        => new()
        {
            StatConfigOperationsMaxActiveJobsPerActor = 1,
            StatConfigOperationsFaultCommandIdPrefix =
                P808FaultCommandPrefix,
            StatConfigOperationsFaultPoints =
                ["CLEANUP_TRANSIENT"]
        };
}

internal sealed record P811OwnedCaseContract(
    string CaseId,
    string Contract,
    IReadOnlyList<string> Oracles);

internal sealed record P811BackendRestartEvidence(
    DateTime StartedAtUtc,
    int PreviousProcessId,
    int PreviousPort,
    bool PreviousStopVerified,
    bool PreviousPortReleaseVerified,
    int ReplacementProcessId,
    int ReplacementPort,
    string DatabaseName,
    string Queue,
    bool ReplacementAuthenticated,
    bool StopVerified,
    bool PortReleaseVerified,
    DateTime? CompletedAtUtc);
