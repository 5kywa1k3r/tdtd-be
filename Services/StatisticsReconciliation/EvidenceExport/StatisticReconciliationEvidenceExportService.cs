using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.EvidenceExport;

public sealed class StatisticReconciliationEvidenceMongoStore(MongoDbContext context)
    : IStatisticReconciliationEvidenceStore
{
    public const string CollectionName =
        "work_report_statistic_reconciliation_exports";
    private readonly IMongoCollection<StatisticReconciliationEvidenceExport> _rows =
        context.Db.GetCollection<StatisticReconciliationEvidenceExport>(CollectionName);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesReady;

    public async Task<StatisticReconciliationEvidenceExport?> FindByIdAsync(
        string id, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        return await _rows.Find(value => value.Id == id).Limit(2)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<StatisticReconciliationEvidenceExport?> FindByCommandAsync(
        string workId, string scopeAssignmentId, string reconciliationId,
        string commandId, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        return await _rows.Find(value => value.WorkId == workId &&
                value.ScopeAssignmentId == scopeAssignmentId &&
                value.ReconciliationId == reconciliationId &&
                value.CommandId == commandId)
            .Limit(2).SingleOrDefaultAsync(ct);
    }

    public async Task<(
        IReadOnlyList<StatisticReconciliationEvidenceExport> Rows,
        long Total)> ListReadableAsync(
        string workId, string scopeAssignmentId, string reconciliationId,
        string permissionSnapshotSha256, DateTime nowUtc,
        int page, int pageSize, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        var filters = Builders<StatisticReconciliationEvidenceExport>.Filter;
        var scope = filters.Eq(value => value.WorkId, workId) &
                    filters.Eq(value => value.ScopeAssignmentId,
                        scopeAssignmentId) &
                    filters.Eq(value => value.ReconciliationId,
                        reconciliationId) &
                    filters.Gt(value => value.ExpiresAtUtc, nowUtc) &
                    (filters.Ne(value => value.DetailLevel,
                         StatisticReconciliationEvidenceDetailLevels.Operator) |
                     filters.Eq(value => value.PermissionSnapshotSha256,
                         permissionSnapshotSha256));
        var total = await _rows.CountDocumentsAsync(scope,
            cancellationToken: ct);
        var rows = await _rows.Find(scope)
            .SortByDescending(value => value.CreatedAtUtc)
            .ThenBy(value => value.Id)
            .Skip(checked((page - 1) * pageSize))
            .Limit(pageSize)
            .ToListAsync(ct);
        return (rows, total);
    }

    public async Task<bool> TryAppendAsync(
        StatisticReconciliationEvidenceExport artifact,
        CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        try
        {
            await _rows.InsertOneAsync(artifact, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<long> CleanupExpiredAsync(string workId,
        string scopeAssignmentId, string reconciliationId, DateTime nowUtc,
        CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        var result = await _rows.DeleteManyAsync(value =>
            value.WorkId == workId &&
            value.ScopeAssignmentId == scopeAssignmentId &&
            value.ReconciliationId == reconciliationId &&
            value.ExpiresAtUtc <= nowUtc, ct);
        return result.DeletedCount;
    }

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (_indexesReady) return;
        await _indexGate.WaitAsync(ct);
        try
        {
            if (_indexesReady) return;
            await _rows.Indexes.CreateManyAsync([
                new CreateIndexModel<StatisticReconciliationEvidenceExport>(
                    Builders<StatisticReconciliationEvidenceExport>.IndexKeys
                        .Ascending(value => value.WorkId)
                        .Ascending(value => value.ScopeAssignmentId)
                        .Ascending(value => value.ReconciliationId)
                        .Ascending(value => value.CommandId),
                    new CreateIndexOptions
                    {
                        Name = "ux_p10_evidence_scope_command",
                        Unique = true
                    }),
                new CreateIndexModel<StatisticReconciliationEvidenceExport>(
                    Builders<StatisticReconciliationEvidenceExport>.IndexKeys
                        .Ascending(value => value.ExpiresAtUtc),
                    new CreateIndexOptions
                    {
                        Name = "ttl_p10_evidence_expiry",
                        ExpireAfter = TimeSpan.Zero
                    }),
                new CreateIndexModel<StatisticReconciliationEvidenceExport>(
                    Builders<StatisticReconciliationEvidenceExport>.IndexKeys
                        .Ascending(value => value.WorkId)
                        .Ascending(value => value.ScopeAssignmentId)
                        .Ascending(value => value.ReconciliationId)
                        .Descending(value => value.CreatedAtUtc)
                        .Ascending(value => value.Id),
                    new CreateIndexOptions
                    {
                        Name = "ix_p10_evidence_scope_created_v1"
                    })
            ], ct);
            _indexesReady = true;
        }
        finally { _indexGate.Release(); }
    }
}

public sealed class StatisticReconciliationEvidenceOwner(
    MongoDbContext context,
    IStatisticReconciliationIndependentReviewOwner reviewOwner,
    IStatisticReconciliationIndependentReviewBackend reviewBackend,
    IStatisticReconciliationEvidenceCandidateGate candidateGate,
    IStatisticReconciliationEvidenceStore store,
    IAppTimeService time) : IStatisticReconciliationEvidenceOwner
{
    public Task<StatisticReconciliationReviewScopeAuthorization?> AuthorizeScopeAsync(
        string? workId, string? scopeAssignmentId, MeResponse actor,
        CancellationToken ct = default)
        => reviewOwner.AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct);

    public async Task<StatisticReconciliationEvidenceArtifactDto> CreateAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        StatisticReconciliationEvidenceCompileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        candidateGate.Require(StatisticReconciliationRouteRegistry.EvidenceCreate);
        command = command with
        {
            ActorId = authorization.ActorId,
            PermissionSnapshotSha256 = authorization.PermissionSnapshotSha256,
            CanViewOperatorDetail = authorization.CanViewOperatorDetail,
            RequestedAtUtc = RequireUtc(time.UtcNow),
            Retention = TimeSpan.FromDays(30)
        };

        var replay = await store.FindByCommandAsync(authorization.WorkId,
            authorization.ScopeAssignmentId, reconciliationId,
            command.CommandId, ct);
        if (replay is not null)
        {
            StatisticReconciliationEvidenceCanonical.RequireStored(replay);
            RequireScope(replay, authorization, reconciliationId);
            if (replay.ExpiresAtUtc <= command.RequestedAtUtc)
                throw Expired();
            return ToDto(replay);
        }

        var snapshot = await ResolveSnapshotAsync(authorization,
            reconciliationId, ct);
        var artifact = StatisticReconciliationEvidenceCanonical.Compile(
            snapshot, command);
        if (!await store.TryAppendAsync(artifact, ct))
        {
            replay = await store.FindByCommandAsync(authorization.WorkId,
                authorization.ScopeAssignmentId, reconciliationId,
                command.CommandId, ct) ?? throw Drift("appendReplay");
            StatisticReconciliationEvidenceCanonical.RequireStored(replay);
            if (!Exact(artifact, replay)) throw Drift("commandCollision");
            artifact = replay;
        }
        return ToDto(artifact);
    }

    public async Task<StatisticReconciliationEvidenceDownload> DownloadAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, string exportId,
        CancellationToken ct = default)
    {
        var artifact = await ResolveReadableAsync(authorization,
            reconciliationId, exportId, ct);
        return new(ToDto(artifact), artifact.Content.ToArray());
    }

    public async Task<StatisticReconciliationEvidenceArtifactPageDto> ListAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, int page, int pageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        candidateGate.Require(StatisticReconciliationRouteRegistry.EvidenceRead);
        if (page < 1 || pageSize is < 1 or > 100)
            throw new StatisticReconciliationEvidenceException(
                StatisticReconciliationEvidenceFailureCodes.InvalidRequest,
                "page");
        var now = RequireUtc(time.UtcNow);
        var result = await store.ListReadableAsync(authorization.WorkId,
            authorization.ScopeAssignmentId, reconciliationId,
            authorization.PermissionSnapshotSha256, now, page, pageSize, ct);
        foreach (var artifact in result.Rows)
        {
            RequireScope(artifact, authorization, reconciliationId);
            StatisticReconciliationEvidenceCanonical.RequireStored(artifact);
            RequireReadable(artifact, authorization, now);
        }
        return new(result.Rows.Select(ToDto).ToArray(), result.Total,
            page, pageSize);
    }

    public async Task<StatisticReconciliationEvidenceArtifactDto> ReadAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, string exportId,
        CancellationToken ct = default)
        => ToDto(await ResolveReadableAsync(authorization,
            reconciliationId, exportId, ct));

    private async Task<StatisticReconciliationEvidenceExport>
        ResolveReadableAsync(
            StatisticReconciliationReviewScopeAuthorization authorization,
            string reconciliationId, string exportId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        candidateGate.Require(StatisticReconciliationRouteRegistry.EvidenceRead);
        var artifact = await store.FindByIdAsync(exportId, ct) ?? throw Denied();
        RequireScope(artifact, authorization, reconciliationId);
        StatisticReconciliationEvidenceCanonical.RequireStored(artifact);
        RequireReadable(artifact, authorization, RequireUtc(time.UtcNow));
        return artifact;
    }

    private static void RequireReadable(
        StatisticReconciliationEvidenceExport artifact,
        StatisticReconciliationReviewScopeAuthorization authorization,
        DateTime nowUtc)
    {
        if (artifact.ExpiresAtUtc <= nowUtc) throw Expired();

        // Every controller request re-authorizes the assignment scope before
        // lookup. Operator artifacts additionally bind the current permission
        // snapshot; redacted artifacts remain readable only while scope access
        // is still present.
        if (artifact.DetailLevel ==
                StatisticReconciliationEvidenceDetailLevels.Operator &&
            (!authorization.CanViewOperatorDetail ||
             authorization.PermissionSnapshotSha256 !=
                artifact.PermissionSnapshotSha256))
            throw Denied();
    }

    private async Task<StatisticReconciliationEvidenceSnapshot> ResolveSnapshotAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, CancellationToken ct)
    {
        // This call performs current-generation validation and reads the review
        // only after scope authorization has succeeded.
        var review = await reviewOwner.ReadAsync(authorization,
            reconciliationId, ct);
        var approval = await reviewOwner.GetFinalApprovalAsync(authorization,
            reconciliationId, ct);
        var run = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == reconciliationId &&
                value.WorkId == authorization.WorkId &&
                value.ScopeAssignmentId == authorization.ScopeAssignmentId &&
                !value.IsDeleted)
            .Limit(2).SingleOrDefaultAsync(ct) ?? throw Denied();
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        if (run.CurrentGenerationId is null || run.CurrentGenerationHash is null)
            throw Target("generation");
        var verdicts = await context.StatisticReconciliationReviews.Find(value =>
                value.ReconciliationId == run.Id &&
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash &&
                value.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict)
            .Limit(2).ToListAsync(ct);
        if (verdicts.Count != 1) throw Target("verdict");
        var verdict = verdicts[0];
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        if (!verdict.Signable || !verdict.CompleteEvidence ||
            approval.GenerationId != verdict.VerdictGenerationId)
            throw Target("signable");

        var observations = await context.StatisticReconciliationObservations
            .Find(value => value.ReconciliationId == run.Id &&
                (value.GenerationId == verdict.ExpectedGenerationId ||
                 value.GenerationId == verdict.ActualGenerationId))
            .SortBy(value => value.GenerationId).ThenBy(value => value.RecordKind)
            .ThenBy(value => value.Id)
            .Limit(StatisticReconciliationEvidenceCanonical.MaximumRows * 8)
            .ToListAsync(ct);
        var expected = observations.Where(value =>
                value.GenerationId == verdict.ExpectedGenerationId &&
                value.RecordKind == StatisticReconciliationObservationRecordKinds.ExpectedAtom &&
                value.Atom is not null)
            .Select(value => value.Atom!).ToArray();
        var actual = observations.Where(value =>
                value.GenerationId == verdict.ActualGenerationId &&
                value.RecordKind == StatisticReconciliationObservationRecordKinds.ActualAtom &&
                value.Atom is not null)
            .Select(value => value.Atom!).ToArray();
        var sourceIds = observations.Where(value =>
                value.GenerationId == verdict.ActualGenerationId &&
                value.RecordKind == StatisticReconciliationObservationRecordKinds.ActualSourceDecision &&
                value.ActualSourceDecision is { Included: true })
            .Select(value => value.ActualSourceDecision!.SourceStableIdentitySha256)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var audit = await reviewBackend.ReadLineageAsync(run.Id, ct);
        var reviewSignature = StatisticReconciliationEvidenceCanonical.HashSequence(
            "P10_EVIDENCE_REVIEW_SIGNATURE_V1",
            audit.OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.DocumentSha256));

        var rows = BuildRows(expected, actual, sourceIds,
            verdict.FreshnessAssessmentSha256 ?? "NONE",
            authorization.PermissionSnapshotSha256, verdict.Verdict);

        var confirmedRun = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == run.Id && !value.IsDeleted).Limit(2)
            .SingleOrDefaultAsync(ct) ?? throw Drift("runMissing");
        var confirmedVerdict = await context.StatisticReconciliationReviews
            .Find(value => value.Id == verdict.Id).Limit(2)
            .SingleOrDefaultAsync(ct) ?? throw Drift("verdictMissing");
        if (!run.ToBson().SequenceEqual(confirmedRun.ToBson()) ||
            !verdict.ToBson().SequenceEqual(confirmedVerdict.ToBson()))
            throw Drift("currentReplay");

        return new(run.WorkId, run.ScopeAssignmentId, run.Id,
            verdict.VerdictGenerationId, verdict.VerdictGenerationSha256,
            verdict.DocumentSemanticSha256, reviewSignature,
            approval.FinalApprovalSha256, RequireUtc(verdict.CreatedAtUtc), rows);
    }

    internal static ImmutableArray<StatisticReconciliationEvidenceRow> BuildRows(
        IReadOnlyList<StatisticReconciliationObservationAtom> expected,
        IReadOnlyList<StatisticReconciliationObservationAtom> actual,
        ImmutableArray<string> sourceIds, string freshness,
        string permission, string verdict)
    {
        var expectedGroups = expected.GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, GroupCell, StringComparer.Ordinal);
        var actualGroups = actual.GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, GroupCell, StringComparer.Ordinal);
        return expectedGroups.Keys.Concat(actualGroups.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var e = expectedGroups.GetValueOrDefault(key) ?? MissingCell();
                var a = actualGroups.GetValueOrDefault(key) ?? MissingCell();
                var match = e == a;
                var delta = new StatisticReconciliationEvidenceCell(
                    "DELTA", "VALUE", JsonSerializer.Serialize(match ? "MATCH" :
                        e.ValueState == "MISSING" ? "EXTRA" :
                        a.ValueState == "MISSING" ? "MISSING" : "DIFFERENT"));
                var atom = expected.Concat(actual).First(value => Key(value) == key);
                return new StatisticReconciliationEvidenceRow(atom.IdentitySha256,
                    $"{atom.Family}/{atom.Kind}/{atom.MetricId}/{atom.AtomKind}",
                    e, a, delta, freshness, permission, verdict, sourceIds);
            }).ToImmutableArray();
    }

    private static string Key(StatisticReconciliationObservationAtom value)
        => string.Join('|', value.IdentitySha256, value.AtomKind,
            value.TransitionLeg ?? "NONE", value.TransitionKind ?? "NONE");

    private static StatisticReconciliationEvidenceCell GroupCell(
        IGrouping<string, StatisticReconciliationObservationAtom> values)
    {
        var ordered = values.OrderBy(value => value.ValueState, StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
            .ThenBy(value => value.DecimalScale).ThenBy(value => value.OccurrenceCount)
            .Select(value => new { value.ValueState, value.CanonicalValue,
                value.DecimalScale, value.OccurrenceCount }).ToArray();
        var first = values.First();
        return new(first.ValueType, ordered.Length == 1 ? ordered[0].ValueState : "MULTI",
            JsonSerializer.Serialize(ordered));
    }

    private static StatisticReconciliationEvidenceCell MissingCell()
        => new("NONE", "MISSING", "null");

    private static void RequireScope(StatisticReconciliationEvidenceExport value,
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId)
    {
        if (value.WorkId != authorization.WorkId ||
            value.ScopeAssignmentId != authorization.ScopeAssignmentId ||
            value.ReconciliationId != reconciliationId)
            throw Denied();
    }

    private static bool Exact(StatisticReconciliationEvidenceExport left,
        StatisticReconciliationEvidenceExport right)
        => left.ToBson().SequenceEqual(right.ToBson());

    private static StatisticReconciliationEvidenceArtifactDto ToDto(
        StatisticReconciliationEvidenceExport value)
    {
        var root = $"/api/works/{Uri.EscapeDataString(value.WorkId)}" +
            $"/statistics/{Uri.EscapeDataString(value.ScopeAssignmentId)}" +
            $"/reconciliations/{Uri.EscapeDataString(value.ReconciliationId)}" +
            $"/evidence-exports/{Uri.EscapeDataString(value.Id)}";
        return new(value.Id, value.ReconciliationId, value.GenerationId, value.Format,
            value.DetailLevel, value.FileName, value.ContentType,
            value.ManifestSha256, value.ContentSha256, value.ContentLength,
            value.CreatedAtUtc, value.ExpiresAtUtc)
        {
            Links =
            [
                new("READBACK", root, "GET"),
                new("DOWNLOAD", root + "/download", "GET")
            ]
        };
    }

    private static DateTime RequireUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : throw Drift("utc");
    private static StatisticReconciliationEvidenceException Denied()
        => new(StatisticReconciliationEvidenceFailureCodes.PermissionDenied, "scope");
    private static StatisticReconciliationEvidenceException Expired()
        => new(StatisticReconciliationEvidenceFailureCodes.Expired, "retention");
    private static StatisticReconciliationEvidenceException Target(string detail)
        => new(StatisticReconciliationEvidenceFailureCodes.TargetNotExportable, detail);
    private static StatisticReconciliationEvidenceException Drift(string detail)
        => new(StatisticReconciliationEvidenceFailureCodes.Drift, detail);
}
