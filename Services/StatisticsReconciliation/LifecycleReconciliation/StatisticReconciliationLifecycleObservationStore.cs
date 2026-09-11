using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

public interface IStatisticReconciliationLifecycleObservationStore
{
    StatisticReconciliationLifecycleAppendResult AppendBatch(
        IEnumerable<StatisticReconciliationLifecycleStoredObservation> observations);

    ImmutableArray<StatisticReconciliationLifecycleStoredObservation> ReadAll(
        string reconciliationId);

    StatisticReconciliationLifecycleAppendResult Inspect(string reconciliationId);
}

public sealed class InMemoryStatisticReconciliationLifecycleObservationStore
    : IStatisticReconciliationLifecycleObservationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string,
        StatisticReconciliationLifecycleStoredObservation> _rows =
        new(StringComparer.Ordinal);

    public StatisticReconciliationLifecycleAppendResult AppendBatch(
        IEnumerable<StatisticReconciliationLifecycleStoredObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var batch = observations.ToArray();
        if (batch.Length == 0)
            throw new StatisticReconciliationLifecycleException(
                StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                "OBSERVATION_BATCH_EMPTY");

        lock (_gate)
        {
            var normalized = batch.Select(Normalize).ToArray();
            var incoming = new Dictionary<string,
                StatisticReconciliationLifecycleStoredObservation>(
                StringComparer.Ordinal);
            foreach (var row in normalized)
            {
                var key = Key(row);
                if (incoming.TryGetValue(key, out var duplicate) &&
                    !SameRow(duplicate, row))
                    Conflict("INCOMING_DUPLICATE_CHANGED");
                incoming[key] = row;
            }

            foreach (var pair in incoming)
            {
                if (_rows.TryGetValue(pair.Key, out var existing) &&
                    !SameRow(existing, pair.Value))
                    Conflict("APPEND_ONLY_ROW_CHANGED");
            }

            var appended = 0;
            var replay = 0;
            foreach (var pair in incoming)
            {
                if (_rows.ContainsKey(pair.Key))
                {
                    replay++;
                    continue;
                }
                _rows.Add(pair.Key, pair.Value);
                appended++;
            }

            var reconciliationId = normalized.Select(value =>
                    value.ReconciliationId)
                .Distinct(StringComparer.Ordinal).Single();
            return BuildResult(reconciliationId, appended, replay);
        }
    }

    public ImmutableArray<StatisticReconciliationLifecycleStoredObservation>
        ReadAll(string reconciliationId)
    {
        reconciliationId = Required(reconciliationId, "RECONCILIATION_ID");
        lock (_gate)
        {
            return _rows.Values.Where(value =>
                    StringComparer.Ordinal.Equals(value.ReconciliationId,
                        reconciliationId))
                .OrderBy(value => value.GenerationId, StringComparer.Ordinal)
                .ThenBy(value => value.RecordKind, StringComparer.Ordinal)
                .ThenBy(value => value.ObservationId, StringComparer.Ordinal)
                .ToImmutableArray();
        }
    }

    public StatisticReconciliationLifecycleAppendResult Inspect(
        string reconciliationId)
    {
        reconciliationId = Required(reconciliationId, "RECONCILIATION_ID");
        lock (_gate)
            return BuildResult(reconciliationId, 0, 0);
    }

    private StatisticReconciliationLifecycleAppendResult BuildResult(
        string reconciliationId, int appended, int replay)
    {
        var rows = _rows.Values.Where(value =>
                StringComparer.Ordinal.Equals(value.ReconciliationId,
                    reconciliationId))
            .OrderBy(value => value.GenerationId, StringComparer.Ordinal)
            .ThenBy(value => value.RecordKind, StringComparer.Ordinal)
            .ThenBy(value => value.ObservationId, StringComparer.Ordinal)
            .ToArray();
        var manifest = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_OBSERVATION_MANIFEST_V1", reconciliationId,
            StatisticReconciliationLifecycleCanonical.HashSequence(
                rows.Select(value => value.SemanticSha256)));
        return new StatisticReconciliationLifecycleAppendResult(
            appended, replay, rows.Length, manifest);
    }

    private static StatisticReconciliationLifecycleStoredObservation Normalize(
        StatisticReconciliationLifecycleStoredObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var reconciliationId = Required(value.ReconciliationId,
            "RECONCILIATION_ID");
        var generationId = Required(value.GenerationId, "GENERATION_ID");
        var recordKind = Required(value.RecordKind, "RECORD_KIND")
            .ToUpperInvariant();
        if (recordKind is not StatisticReconciliationLifecycleRecordKinds.Source and
            not StatisticReconciliationLifecycleRecordKinds.Actual and
            not StatisticReconciliationLifecycleRecordKinds.Result)
            throw new StatisticReconciliationLifecycleException(
                StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                "RECORD_KIND_INVALID");
        return value with
        {
            ReconciliationId = reconciliationId,
            GenerationId = generationId,
            RecordKind = recordKind,
            ObservationId = Required(value.ObservationId, "OBSERVATION_ID"),
            SemanticSha256 = Sha(value.SemanticSha256, "SEMANTIC_SHA256"),
            PayloadSha256 = Sha(value.PayloadSha256, "PAYLOAD_SHA256")
        };
    }

    private static string Key(
        StatisticReconciliationLifecycleStoredObservation value)
        => $"{value.ReconciliationId}\n{value.GenerationId}\n{value.RecordKind}\n{value.ObservationId}";

    private static bool SameRow(
        StatisticReconciliationLifecycleStoredObservation left,
        StatisticReconciliationLifecycleStoredObservation right)
        => left == right;

    private static void Conflict(string detail)
        => throw new StatisticReconciliationLifecycleException(
            StatisticReconciliationLifecycleFailureCodes.ObservationConflict,
            detail);

    private static string Required(string? value, string path)
        => StatisticReconciliationLifecycleCanonical.Required(value, path);
    private static string Sha(string? value, string path)
        => StatisticReconciliationLifecycleCanonical.Sha(value, path);
}
