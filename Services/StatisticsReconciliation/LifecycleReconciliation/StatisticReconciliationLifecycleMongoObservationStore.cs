using System.Collections.Immutable;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

public sealed class StatisticReconciliationLifecycleMongoObservationStore(
    MongoDbContext context) : IStatisticReconciliationLifecycleObservationStore
{
    public const string CollectionName =
        "work_report_statistic_reconciliation_observations";

    private readonly IMongoCollection<StatisticReconciliationLifecycleStoredObservation> _rows =
        context.Db.GetCollection<StatisticReconciliationLifecycleStoredObservation>(CollectionName);
    private readonly object _indexGate = new();
    private bool _indexesReady;

    public StatisticReconciliationLifecycleAppendResult AppendBatch(
        IEnumerable<StatisticReconciliationLifecycleStoredObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        EnsureIndexes();
        var batch = observations.ToArray();
        if (batch.Length == 0 || batch.Select(value => value.ReconciliationId)
                .Distinct(StringComparer.Ordinal).Count() != 1)
            throw Error("OBSERVATION_BATCH_INVALID");
        if (batch.Select(Key).Distinct(StringComparer.Ordinal).Count() != batch.Length)
            throw Error("OBSERVATION_BATCH_DUPLICATE");

        using var session = context.Db.Client.StartSession();
        session.StartTransaction();
        try
        {
            var replay = 0;
            var append = new List<StatisticReconciliationLifecycleStoredObservation>();
            foreach (var row in batch)
            {
                Validate(row);
                var existing = _rows.Find(session, value =>
                        value.ReconciliationId == row.ReconciliationId &&
                        value.GenerationId == row.GenerationId &&
                        value.RecordKind == row.RecordKind &&
                        value.ObservationId == row.ObservationId)
                    .FirstOrDefault();
                if (existing is null) append.Add(row);
                else if (existing == row) replay++;
                else throw Error("APPEND_ONLY_ROW_CHANGED");
            }
            if (append.Count > 0)
                _rows.InsertMany(session, append, new InsertManyOptions { IsOrdered = true });
            session.CommitTransaction();
            return BuildResult(batch[0].ReconciliationId, append.Count, replay);
        }
        catch
        {
            if (session.IsInTransaction) session.AbortTransaction();
            throw;
        }
    }

    public System.Collections.Immutable.ImmutableArray<
        StatisticReconciliationLifecycleStoredObservation> ReadAll(string reconciliationId)
    {
        EnsureIndexes();
        reconciliationId = StatisticReconciliationLifecycleCanonical.Required(
            reconciliationId, "RECONCILIATION_ID");
        return _rows.Find(value => value.ReconciliationId == reconciliationId)
            .SortBy(value => value.GenerationId).ThenBy(value => value.RecordKind)
            .ThenBy(value => value.ObservationId).ToList().ToImmutableArray();
    }

    public StatisticReconciliationLifecycleAppendResult Inspect(string reconciliationId)
        => BuildResult(StatisticReconciliationLifecycleCanonical.Required(
            reconciliationId, "RECONCILIATION_ID"), 0, 0);

    private StatisticReconciliationLifecycleAppendResult BuildResult(
        string reconciliationId, int appended, int replay)
    {
        var rows = ReadAll(reconciliationId);
        return new(appended, replay, rows.Length,
            StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_OBSERVATION_MANIFEST_V1", reconciliationId,
                StatisticReconciliationLifecycleCanonical.HashSequence(
                    rows.Select(value => value.SemanticSha256))));
    }

    private void EnsureIndexes()
    {
        if (_indexesReady) return;
        lock (_indexGate)
        {
            if (_indexesReady) return;
            _rows.Indexes.CreateMany([
                new CreateIndexModel<StatisticReconciliationLifecycleStoredObservation>(
                    Builders<StatisticReconciliationLifecycleStoredObservation>.IndexKeys
                        .Ascending(value => value.ReconciliationId)
                        .Ascending(value => value.GenerationId)
                        .Ascending(value => value.RecordKind)
                        .Ascending(value => value.ObservationId),
                    new CreateIndexOptions
                    {
                        Name = "ux_p10Lifecycle_generation_kind_observation", Unique = true
                    }),
                new CreateIndexModel<StatisticReconciliationLifecycleStoredObservation>(
                    Builders<StatisticReconciliationLifecycleStoredObservation>.IndexKeys
                        .Ascending(value => value.ReconciliationId)
                        .Ascending(value => value.GenerationId),
                    new CreateIndexOptions { Name = "ix_p10Lifecycle_lineage" })
            ]);
            _indexesReady = true;
        }
    }

    private static void Validate(StatisticReconciliationLifecycleStoredObservation value)
    {
        _ = StatisticReconciliationLifecycleCanonical.Required(value.ReconciliationId,
            "RECONCILIATION_ID");
        _ = StatisticReconciliationLifecycleCanonical.Required(value.GenerationId,
            "GENERATION_ID");
        _ = StatisticReconciliationLifecycleCanonical.Required(value.ObservationId,
            "OBSERVATION_ID");
        _ = StatisticReconciliationLifecycleCanonical.Sha(value.SemanticSha256,
            "SEMANTIC_SHA256");
        _ = StatisticReconciliationLifecycleCanonical.Sha(value.PayloadSha256,
            "PAYLOAD_SHA256");
        if (value.RecordKind is not StatisticReconciliationLifecycleRecordKinds.Source and
            not StatisticReconciliationLifecycleRecordKinds.Actual and
            not StatisticReconciliationLifecycleRecordKinds.Result)
            throw Error("RECORD_KIND_INVALID");
    }

    private static string Key(StatisticReconciliationLifecycleStoredObservation value)
        => $"{value.ReconciliationId}\n{value.GenerationId}\n{value.RecordKind}\n{value.ObservationId}";

    private static StatisticReconciliationLifecycleException Error(string detail)
        => new(StatisticReconciliationLifecycleFailureCodes.ObservationConflict, detail);
}
