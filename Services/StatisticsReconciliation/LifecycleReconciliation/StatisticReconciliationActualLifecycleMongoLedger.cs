using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal sealed class StatisticReconciliationActualLifecycleMongoLedger(
    MongoDbContext context) : IStatisticReconciliationActualLifecycleLedger
{
    internal const int MaxObservationRows = 200_000;
    internal const string CollectionName =
        "work_report_statistic_reconciliation_observations";

    private readonly IMongoCollection<StatisticReconciliationActualLifecycleLedgerRow>
        _rows = context.Db.GetCollection<
            StatisticReconciliationActualLifecycleLedgerRow>(CollectionName);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesReady;

    public async Task<StatisticReconciliationActualLifecycleAppendResult> AppendAsync(
        StatisticReconciliationActualLifecycleEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        StatisticReconciliationActualLifecycleEvidenceIntegrity.Validate(evidence);
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        if (evidence.Rows.IsDefaultOrEmpty ||
            evidence.Rows.Length > MaxObservationRows ||
            evidence.Rows.Select(static row => row.Id)
                .Distinct(StringComparer.Ordinal).Count() != evidence.Rows.Length)
        {
            throw new InvalidOperationException(
                "P10 lifecycle append batch is invalid.");
        }

        using var session = await context.Db.Client.StartSessionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        session.StartTransaction();
        try
        {
            var existing = await _rows.Find(
                    session,
                    OwnedGenerationFilter(
                        evidence.Manifest.ReconciliationId,
                        evidence.Manifest.BaseCoherentGenerationId,
                        evidence.Manifest.BaseCoherentGenerationSha256))
                .SortBy(static row => row.RecordKind)
                .ThenBy(static row => row.ObservationId)
                .Limit(MaxObservationRows + 1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing.Count != 0)
            {
                RequireExactReplay(evidence, existing);
                await session.AbortTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
                return Result(evidence, appended: false, replayed: true);
            }

            await _rows.InsertManyAsync(
                session,
                evidence.Rows,
                new InsertManyOptions { IsOrdered = true },
                cancellationToken).ConfigureAwait(false);
            await session.CommitTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            return Result(evidence, appended: true, replayed: false);
        }
        catch (MongoException exception) when (IsDuplicateKey(exception))
        {
            if (session.IsInTransaction)
            {
                await session.AbortTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            var replay = await ReadAndValidateAsync(
                    evidence.Manifest.ReconciliationId,
                    evidence.Manifest.BaseCoherentGenerationId,
                    evidence.Manifest.BaseCoherentGenerationSha256,
                    evidence.Manifest.ManifestSha256,
                    evidence.Manifest.ObservationCount,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is null)
            {
                throw new InvalidOperationException(
                    "P10 lifecycle append-only replay conflicts with durable rows.",
                    exception);
            }
            RequireExactReplay(evidence, replay.Rows);
            return Result(evidence, appended: false, replayed: true);
        }
        catch
        {
            if (session.IsInTransaction)
            {
                await session.AbortTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<StatisticReconciliationActualLifecycleEvidence?>
        ReadAndValidateCoreAsync(
            string reconciliationId,
            string baseCoherentGenerationId,
            string baseCoherentGenerationSha256,
            string manifestSha256,
            int observationCount,
            bool ensureIndexes,
            CancellationToken cancellationToken)
    {
        if (ensureIndexes)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        }
        reconciliationId = StatisticReconciliationLifecycleCanonical.Required(
            reconciliationId,
            nameof(reconciliationId));
        baseCoherentGenerationId =
            StatisticReconciliationLifecycleCanonical.Required(
                baseCoherentGenerationId,
                nameof(baseCoherentGenerationId));
        baseCoherentGenerationSha256 =
            StatisticReconciliationLifecycleCanonical.Sha(
                baseCoherentGenerationSha256,
                nameof(baseCoherentGenerationSha256));
        manifestSha256 = StatisticReconciliationLifecycleCanonical.Sha(
            manifestSha256,
            nameof(manifestSha256));
        if (observationCount <= 0 || observationCount > MaxObservationRows)
        {
            throw new InvalidOperationException(
                "P10 lifecycle observation count is outside bounds.");
        }

        var rows = await _rows.Find(OwnedGenerationFilter(
                reconciliationId,
                baseCoherentGenerationId,
                baseCoherentGenerationSha256))
            .SortBy(static row => row.RecordKind)
            .ThenBy(static row => row.ObservationId)
            .Limit(MaxObservationRows + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }
        if (rows.Count != observationCount)
        {
            throw new InvalidOperationException(
                "P10 lifecycle observation cardinality drifted.");
        }
        var evidence = StatisticReconciliationActualLifecycleEvidenceIntegrity
            .Rehydrate(rows.ToImmutableArray());
        if (!string.Equals(
                evidence.Manifest.ManifestSha256,
                manifestSha256,
                StringComparison.Ordinal) ||
            evidence.Manifest.ObservationCount != observationCount)
        {
            throw new InvalidOperationException(
                "P10 lifecycle manifest binding drifted.");
        }
        return evidence;
    }

    private async Task<StatisticReconciliationActualLifecycleEvidence?>
        ReadAndValidateByManifestCoreAsync(
            string reconciliationId,
            string manifestSha256,
            int observationCount,
            bool ensureIndexes,
            CancellationToken cancellationToken)
    {
        if (ensureIndexes)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        }
        reconciliationId = StatisticReconciliationLifecycleCanonical.Required(
            reconciliationId,
            nameof(reconciliationId));
        manifestSha256 = StatisticReconciliationLifecycleCanonical.Sha(
            manifestSha256,
            nameof(manifestSha256));
        if (observationCount <= 0 || observationCount > MaxObservationRows)
        {
            throw new InvalidOperationException(
                "P10 lifecycle observation count is outside bounds.");
        }

        var fb = Builders<StatisticReconciliationActualLifecycleLedgerRow>.Filter;
        var manifests = await _rows.Find(fb.And(
                fb.Eq(static row => row.SchemaVersion,
                    StatisticReconciliationActualLifecycleLedgerSchema.Version),
                fb.Eq(static row => row.RecordKind,
                    StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind),
                fb.Eq(static row => row.ReconciliationId, reconciliationId),
                fb.Eq(static row => row.SemanticSha256, manifestSha256)))
            .SortBy(static row => row.Id)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (manifests.Count == 0)
        {
            return null;
        }
        if (manifests.Count != 1)
        {
            throw new InvalidOperationException(
                "P10 lifecycle manifest cardinality is ambiguous.");
        }
        var envelope = manifests[0];
        return await ReadAndValidateCoreAsync(
                reconciliationId,
                envelope.BaseCoherentGenerationId,
                envelope.BaseCoherentGenerationSha256,
                manifestSha256,
                observationCount,
                ensureIndexes,
                cancellationToken)
            .ConfigureAwait(false);
    }


    // Admission validates existing durable evidence without creating indexes or
    // invoking any append path. Normal publication retains its index setup.
    internal static IStatisticReconciliationActualLifecycleLedger ForReadOnlyAdmission(
        MongoDbContext context) => new ExistingDataReader(
            new StatisticReconciliationActualLifecycleMongoLedger(context));

    public Task<StatisticReconciliationActualLifecycleEvidence?> ReadAndValidateAsync(
        string reconciliationId, string baseCoherentGenerationId,
        string baseCoherentGenerationSha256, string manifestSha256,
        int observationCount, CancellationToken cancellationToken = default) =>
        ReadAndValidateCoreAsync(reconciliationId, baseCoherentGenerationId,
            baseCoherentGenerationSha256, manifestSha256, observationCount,
            ensureIndexes: true, cancellationToken);

    public Task<StatisticReconciliationActualLifecycleEvidence?> ReadAndValidateByManifestAsync(
        string reconciliationId, string manifestSha256, int observationCount,
        CancellationToken cancellationToken = default) =>
        ReadAndValidateByManifestCoreAsync(reconciliationId, manifestSha256,
            observationCount, ensureIndexes: true, cancellationToken);

    private sealed class ExistingDataReader(
        StatisticReconciliationActualLifecycleMongoLedger owner)
        : IStatisticReconciliationActualLifecycleLedger
    {
        public Task<StatisticReconciliationActualLifecycleAppendResult> AppendAsync(
            StatisticReconciliationActualLifecycleEvidence evidence,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "P11 admission lifecycle reader cannot append evidence.");

        public Task<StatisticReconciliationActualLifecycleEvidence?> ReadAndValidateAsync(
            string reconciliationId, string baseCoherentGenerationId,
            string baseCoherentGenerationSha256, string manifestSha256,
            int observationCount, CancellationToken cancellationToken = default) =>
            owner.ReadAndValidateCoreAsync(reconciliationId, baseCoherentGenerationId,
                baseCoherentGenerationSha256, manifestSha256, observationCount,
                ensureIndexes: false, cancellationToken);

        public Task<StatisticReconciliationActualLifecycleEvidence?> ReadAndValidateByManifestAsync(
            string reconciliationId, string manifestSha256, int observationCount,
            CancellationToken cancellationToken = default) =>
            owner.ReadAndValidateByManifestCoreAsync(reconciliationId, manifestSha256,
                observationCount, ensureIndexes: false, cancellationToken);
    }

    private static bool IsDuplicateKey(MongoException exception) =>
        exception switch
        {
            MongoWriteException write =>
                write.WriteError?.Category == ServerErrorCategory.DuplicateKey,
            MongoBulkWriteException<StatisticReconciliationActualLifecycleLedgerRow> bulk =>
                bulk.WriteErrors.Any(static error =>
                    error.Category == ServerErrorCategory.DuplicateKey),
            _ => false
        };

    internal static FilterDefinition<StatisticReconciliationActualLifecycleLedgerRow>
        OwnedGenerationFilter(
            string reconciliationId,
            string baseCoherentGenerationId,
            string baseCoherentGenerationSha256)
    {
        var fb = Builders<StatisticReconciliationActualLifecycleLedgerRow>.Filter;
        return fb.And(
            fb.Eq(static row => row.SchemaVersion,
                StatisticReconciliationActualLifecycleLedgerSchema.Version),
            fb.In(static row => row.RecordKind,
                StatisticReconciliationActualLifecycleLedgerSchema.Kinds),
            fb.Eq(static row => row.ReconciliationId, reconciliationId),
            fb.Eq(static row => row.BaseCoherentGenerationId,
                baseCoherentGenerationId),
            fb.Eq(static row => row.BaseCoherentGenerationSha256,
                baseCoherentGenerationSha256));
    }

    internal static bool IsOwnedEnvelope(BsonDocument value) =>
        value.TryGetValue("schemaVersion", out var schema) &&
        schema.IsString &&
        string.Equals(
            schema.AsString,
            StatisticReconciliationActualLifecycleLedgerSchema.Version,
            StringComparison.Ordinal) &&
        value.TryGetValue("recordKind", out var kind) &&
        kind.IsString &&
        StatisticReconciliationActualLifecycleLedgerSchema.IsOwnedKind(
            kind.AsString);

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexesReady)
        {
            return;
        }
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexesReady)
            {
                return;
            }
            var fb = Builders<StatisticReconciliationActualLifecycleLedgerRow>.Filter;
            var owned = fb.And(
                fb.Eq(static row => row.SchemaVersion,
                    StatisticReconciliationActualLifecycleLedgerSchema.Version),
                fb.In(static row => row.RecordKind,
                    StatisticReconciliationActualLifecycleLedgerSchema.Kinds));
            var manifest = fb.And(
                fb.Eq(static row => row.SchemaVersion,
                    StatisticReconciliationActualLifecycleLedgerSchema.Version),
                fb.Eq(static row => row.RecordKind,
                    StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind));
            await _rows.Indexes.CreateManyAsync(
                [
                    new CreateIndexModel<
                        StatisticReconciliationActualLifecycleLedgerRow>(
                        Builders<StatisticReconciliationActualLifecycleLedgerRow>
                            .IndexKeys
                            .Ascending(static row => row.ReconciliationId)
                            .Ascending(static row => row.BaseCoherentGenerationId)
                            .Ascending(static row => row.RecordKind)
                            .Ascending(static row => row.ObservationId),
                        new CreateIndexOptions<
                            StatisticReconciliationActualLifecycleLedgerRow>
                        {
                            Name = "ux_p10LifecycleV3_generation_kind_observation",
                            Unique = true,
                            PartialFilterExpression = owned
                        }),
                    new CreateIndexModel<
                        StatisticReconciliationActualLifecycleLedgerRow>(
                        Builders<StatisticReconciliationActualLifecycleLedgerRow>
                            .IndexKeys
                            .Ascending(static row => row.ReconciliationId)
                            .Ascending(static row => row.RecordKind)
                            .Ascending(static row => row.SemanticSha256),
                        new CreateIndexOptions<
                            StatisticReconciliationActualLifecycleLedgerRow>
                        {
                            Name = "ux_p10LifecycleV3_manifest",
                            Unique = true,
                            PartialFilterExpression = manifest
                        })
                ],
                cancellationToken).ConfigureAwait(false);
            _indexesReady = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private static void RequireExactReplay(
        StatisticReconciliationActualLifecycleEvidence expected,
        IEnumerable<StatisticReconciliationActualLifecycleLedgerRow> observed)
    {
        var expectedRows = expected.Rows.ToDictionary(
            static row => row.Id,
            static row => row.DocumentSemanticSha256,
            StringComparer.Ordinal);
        var observedRows = observed.ToArray();
        if (observedRows.Length != expectedRows.Count ||
            observedRows.Any(row => !expectedRows.TryGetValue(
                row.Id,
                out var documentSha) ||
                !string.Equals(
                    row.DocumentSemanticSha256,
                    documentSha,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "P10 lifecycle append-only replay changed durable evidence.");
        }
        var replay = StatisticReconciliationActualLifecycleEvidenceIntegrity
            .Rehydrate(observedRows.ToImmutableArray());
        if (!string.Equals(
                replay.Manifest.ManifestSha256,
                expected.Manifest.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P10 lifecycle append-only replay changed its manifest.");
        }
    }

    private static StatisticReconciliationActualLifecycleAppendResult Result(
        StatisticReconciliationActualLifecycleEvidence evidence,
        bool appended,
        bool replayed) => new(
        appended,
        replayed,
        evidence.Manifest.ManifestSha256,
        evidence.Manifest.ObservationCount,
        evidence.Manifest.LifecycleRowSetSha256);
}
