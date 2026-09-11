using System.Collections.Immutable;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class P10RaceFixture
{
    internal const string RunId = "0123456789abcdef01234567";
    internal const string ActorId = "1123456789abcdef01234567";
    internal const string WorkId = "2123456789abcdef01234567";
    internal const string ScopeId = "3123456789abcdef01234567";

    internal static string H(char value) => new(value, 64);

    internal static (
        StatisticReconciliationRecheckStateMachine Machine,
        StatisticReconciliationBeginRecheckCommand Command,
        StatisticReconciliationRecheckCaptureBinding Binding)
        Recheck(char seed, int maxRetry = 3,
            Func<string>? claimTokenFactory = null)
    {
        var initial = StatisticReconciliationRecheckStateMachine
            .CreateTerminal(
                RunId,
                StatisticReconciliationRunStatuses.Matched,
                H(seed),
                H(seed),
                10,
                2,
                maxRetry,
                P10RaceContract.FixedUtc.AddDays(1));
        return (new(initial, claimTokenFactory ?? (() => H('f'))),
            new("recheck-command", initial.StateRevision,
                initial.StateHash),
            CaptureBinding(seed == 'f' ? 'e' : 'f'));
    }

    internal static StatisticReconciliationReviewPermission Permission(
        string actorId = "reviewer",
        bool authenticated = true,
        bool scopeAuthorized = true,
        bool canReview = true,
        bool canViewOperatorDetail = true,
        char permissionSeed = 'a')
        => new(true, authenticated, scopeAuthorized, canReview,
            canViewOperatorDetail, actorId, H(permissionSeed), 2,
            canViewOperatorDetail ? 2 : 1);

    internal static StatisticReconciliationReviewGeneration ReviewGeneration(
        long revision = 5,
        char generationSeed = '8',
        char generationHashSeed = '9',
        char semanticSeed = 'a')
    {
        var columns = new StatisticReconciliationEightColumnRecord(
            H('1'), H('2'), H('3'), H('4'), H('5'), H('6'), H('7'),
            "MATCHED");
        return new(RunId, H(generationSeed), H(generationHashSeed),
            H(semanticSeed), columns,
            StatisticReconciliationIndependentReviewCanonical
                .ReviewRecordHash(columns),
            "MATCHED", true, true, false,
            "initiator", "writer", revision);
    }

    internal static StatisticReconciliationReviewCommand ReviewCommand(
        StatisticReconciliationReviewGeneration generation,
        string commandId,
        string gate,
        long? expectedRevision = null)
    {
        var command = new StatisticReconciliationReviewCommand(
            commandId, H('0'), RunId,
            generation.GenerationId, generation.GenerationSha256,
            generation.SemanticVerdictSha256, gate,
            StatisticReconciliationReviewDecisions.Approve,
            expectedRevision ?? generation.StateRevision);
        return command with
        {
            RequestSha256 =
                StatisticReconciliationIndependentReviewCanonical
                    .CommandHash(command)
        };
    }

    internal static StatisticReconciliationEvidenceSnapshot EvidenceSnapshot(
        string identity = "metric-identity",
        string actualJson = "1")
    {
        var cellExpected = new StatisticReconciliationEvidenceCell(
            "DECIMAL", "VALUE", "1");
        var cellActual = new StatisticReconciliationEvidenceCell(
            "DECIMAL", "VALUE", actualJson);
        var cellDelta = new StatisticReconciliationEvidenceCell(
            "DECIMAL", "VALUE", "0");
        var row = new StatisticReconciliationEvidenceRow(
            identity, "config-v1", cellExpected, cellActual, cellDelta,
            "FRESH", "AUTHORIZED", "MATCHED",
            ImmutableArray.Create("source-secret-stable-id"));
        return new(WorkId, ScopeId, RunId, H('8'), H('9'), H('a'),
            H('b'), H('c'), P10RaceContract.FixedUtc,
            ImmutableArray.Create(row));
    }

    internal static StatisticReconciliationEvidenceExport CompileEvidence(
        string format = StatisticReconciliationEvidenceFormats.Json,
        bool includeOperatorDetail = false,
        string identity = "metric-identity")
        => StatisticReconciliationEvidenceCanonical.Compile(
            EvidenceSnapshot(identity),
            new StatisticReconciliationEvidenceCompileCommand(
                "export-command", format, includeOperatorDetail, ActorId,
                H('d'), includeOperatorDetail,
                P10RaceContract.FixedUtc, TimeSpan.FromDays(1)));

    private static StatisticReconciliationRecheckCaptureBinding
        CaptureBinding(char seed)
    {
        var oid = $"{seed}123456789abcdef01234567";
        var config = H(seed);
        var plan = new StatisticReconciliationActualCapturePlan
        {
            BoundaryRegistryVersion =
                StatisticReconciliationActualCapturePlanIntegrity
                    .BoundaryRegistryVersion,
            ActualConfigurationBundleSha256 = config,
            Basic = new()
            {
                SnapshotId = oid,
                Mode = "FLOW_FINAL",
                ImmutableSelectorSha256 = H('1')
            },
            Advanced = new()
            {
                SectionId = "section",
                DayNodeIds = ["2123456789abcdef01234567"],
                MonthNodeIds = ["3123456789abcdef01234567"],
                YearNodeIds = ["4123456789abcdef01234567"],
                ImmutableSelectorSha256 = H('2')
            },
            Diff = new()
            {
                ResultId = "5123456789abcdef01234567",
                RunId = "6123456789abcdef01234567",
                ImmutableSelectorSha256 = H('3')
            },
            Api = new()
            {
                Surface = "DIRECT_FIELD",
                OwnerResultId = oid,
                ExpectedTotalRows = 0,
                PageSize = StatisticReconciliationActualCapturePlanIntegrity
                    .ApiPageSize,
                PageCount = 1
            },
            Export = new()
            {
                ExportId = "export-id",
                ResultKind = "DIRECT",
                WorkId = "7123456789abcdef01234567",
                ScopeType = "ASSIGNMENT",
                ScopeId = "8123456789abcdef01234567",
                ResultId = oid,
                RequestSha256 = H('4'),
                AuthorizationSnapshotSha256 = H('5'),
                ContentSha256 = H('6'),
                ColumnManifestSha256 = H('7'),
                OwnerSemanticSha256 = H('8')
            }
        };
        plan.PlanSha256 =
            StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
        var binding = new StatisticReconciliationRecheckCaptureBinding
        {
            P9ResultId = oid,
            P9RunId = oid,
            P9GenerationId = H(seed),
            P9GenerationHash = H(seed),
            P9RunKind = "LIFECYCLE_DIRECT_PROJECTION",
            P9CapabilityId = "P9",
            P9RouteId = "route",
            P9CandidateChainId = "chain",
            P9CandidatePromptId = "prompt",
            SourceReportId = "9123456789abcdef01234567",
            SourcePayloadRevision = 1,
            SourcePayloadHash = H('9'),
            SourceLifecycleRevision = 1,
            SourceLifecycleEventKey = H('a'),
            SourceLifecycleHash = H('b'),
            SourceLifecycleStatus = "APPROVED",
            DynamicFormVersionId = "a123456789abcdef01234567",
            DynamicFormSchemaHash = H('c'),
            P8ConfigBundleHash = H('d'),
            P9CatalogVersion = "v1",
            P9CatalogRawSha256 = H('e'),
            P9CatalogSemanticSha256 = H('f'),
            P9SchemaRawSha256 = H('0'),
            P9SchemaSemanticSha256 = H('1'),
            P9StageLockSha256 = H('2'),
            PeriodKey = "2026",
            PeriodInstanceKey = "2026",
            PeriodKind = "YEAR",
            PeriodStartUtc = P10RaceContract.FixedUtc,
            PeriodEndUtc = P10RaceContract.FixedUtc.AddYears(1),
            TimeAxis = "UTC_GREGORIAN",
            ActualCapturePlan = plan,
            ActualCapturePlanSha256 = plan.PlanSha256,
            ActualConfigurationBundleSha256 = config
        };
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(binding);
        StatisticReconciliationRecheckCaptureBindingCanonical
            .RequireValid(binding);
        return binding;
    }
}

internal enum P10AppendOutcome
{
    Appended,
    Replayed,
    Conflict
}

internal sealed class P10AtomicReceiptStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (string Request, string Payload)>
        _receipts = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (_sync)
                return _receipts.Count;
        }
    }

    internal P10AppendOutcome Append(
        string commandId,
        string requestSha,
        string payloadSha)
    {
        lock (_sync)
        {
            if (!_receipts.TryGetValue(commandId, out var stored))
            {
                _receipts.Add(commandId, (requestSha, payloadSha));
                return P10AppendOutcome.Appended;
            }

            return stored == (requestSha, payloadSha)
                ? P10AppendOutcome.Replayed
                : P10AppendOutcome.Conflict;
        }
    }
}

internal sealed class P10RevisionPublicationStore
{
    private readonly object _sync = new();
    private long _revision;
    private string? _published;

    internal P10RevisionPublicationStore(long revision) =>
        _revision = revision;

    internal bool TryPublish(long expectedRevision, string semanticSha)
    {
        lock (_sync)
        {
            if (_revision != expectedRevision)
                return false;
            _published = semanticSha;
            _revision++;
            return true;
        }
    }

    internal (long Revision, string? Published) Read()
    {
        lock (_sync)
            return (_revision, _published);
    }
}

internal sealed class P10GenerationCommitStore
{
    private readonly Dictionary<string, string> _staged =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _committed =
        new(StringComparer.Ordinal);

    internal void Stage(string generationId, string semanticSha) =>
        _staged[generationId] = semanticSha;

    internal void Commit(string generationId)
    {
        if (!_staged.Remove(generationId, out var semanticSha))
            throw new InvalidOperationException("STAGED_GENERATION_MISSING");
        _committed.Add(generationId, semanticSha);
    }

    internal string? ReadVisible(string generationId) =>
        _committed.GetValueOrDefault(generationId);

    internal int RecoverUncommitted()
    {
        var count = _staged.Count;
        _staged.Clear();
        return count;
    }

    internal int OrphanCount => _staged.Count;
}

internal sealed class P10AtomicEvidenceStore
    : IStatisticReconciliationEvidenceStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, StatisticReconciliationEvidenceExport>
        _artifacts = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (_sync)
                return _artifacts.Count;
        }
    }

    public Task<StatisticReconciliationEvidenceExport?> FindByIdAsync(
        string id,
        CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_artifacts.GetValueOrDefault(id));
    }

    public Task<StatisticReconciliationEvidenceExport?> FindByCommandAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        string commandId,
        CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_artifacts.Values.SingleOrDefault(value =>
                value.WorkId == workId &&
                value.ScopeAssignmentId == scopeAssignmentId &&
                value.ReconciliationId == reconciliationId &&
                value.CommandId == commandId));
    }

    public Task<bool> TryAppendAsync(
        StatisticReconciliationEvidenceExport artifact,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_artifacts.ContainsKey(artifact.Id) ||
                _artifacts.Values.Any(value =>
                    value.WorkId == artifact.WorkId &&
                    value.ScopeAssignmentId == artifact.ScopeAssignmentId &&
                    value.ReconciliationId == artifact.ReconciliationId &&
                    value.CommandId == artifact.CommandId))
                return Task.FromResult(false);
            StatisticReconciliationEvidenceCanonical.RequireStored(artifact);
            _artifacts.Add(artifact.Id, artifact);
            return Task.FromResult(true);
        }
    }

    public Task<long> CleanupExpiredAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            var ids = _artifacts.Values.Where(value =>
                    value.WorkId == workId &&
                    value.ScopeAssignmentId == scopeAssignmentId &&
                    value.ReconciliationId == reconciliationId &&
                    value.ExpiresAtUtc <= nowUtc)
                .Select(value => value.Id).ToArray();
            foreach (var id in ids)
                _artifacts.Remove(id);
            return Task.FromResult((long)ids.Length);
        }
    }
}

internal sealed class P10CountingReviewBackend
    : IStatisticReconciliationIndependentReviewBackend
{
    private readonly StatisticReconciliationIndependentReviewInMemoryBackend
        _inner = new();
    private int _readCalls;
    private int _writeCalls;

    internal int ReadCalls => Volatile.Read(ref _readCalls);
    internal int WriteCalls => Volatile.Read(ref _writeCalls);

    public Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadLineageAsync(string reconciliationId,
            CancellationToken ct = default)
    {
        Interlocked.Increment(ref _readCalls);
        return _inner.ReadLineageAsync(reconciliationId, ct);
    }

    public Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadOperationAsync(string operationCommandId,
            CancellationToken ct = default)
    {
        Interlocked.Increment(ref _readCalls);
        return _inner.ReadOperationAsync(operationCommandId, ct);
    }

    public Task<StatisticReconciliationReviewAppendOutcome>
        TryAppendDecisionAsync(
            StatisticReconciliationIndependentReviewAuditRecord record,
            CancellationToken ct = default)
    {
        Interlocked.Increment(ref _writeCalls);
        return _inner.TryAppendDecisionAsync(record, ct);
    }

    public Task<StatisticReconciliationReviewAppendOutcome>
        TryAppendSupersessionsAsync(
            IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>
                records,
            CancellationToken ct = default)
    {
        Interlocked.Increment(ref _writeCalls);
        return _inner.TryAppendSupersessionsAsync(records, ct);
    }
}

internal sealed class P10ScopedCleanupStore
{
    private readonly HashSet<(string Owner, string Scope, string Id)> _items =
        [];

    internal void Add(string owner, string scope, string id) =>
        _items.Add((owner, scope, id));

    internal int DryRun(string owner, string scope) =>
        _items.Count(value => value.Owner == owner && value.Scope == scope);

    internal int Apply(string owner, string scope) =>
        _items.RemoveWhere(value =>
            value.Owner == owner && value.Scope == scope);

    internal bool Contains(string owner, string scope, string id) =>
        _items.Contains((owner, scope, id));
}
