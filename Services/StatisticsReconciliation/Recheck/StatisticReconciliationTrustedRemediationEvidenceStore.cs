using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal sealed class StatisticReconciliationTrustedRemediationEvidenceStore(
    MongoDbContext context)
{
    private readonly IMongoCollection<
        StatisticReconciliationTrustedRemediationEvidence> _rows =
        context.Db.GetCollection<
            StatisticReconciliationTrustedRemediationEvidence>(
                StatisticReconciliationTrustedRemediationEvidenceCanonical
                    .CollectionName);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesReady;

    internal async Task<(
        StatisticReconciliationTrustedRemediationEvidence Evidence,
        bool Replayed)> AppendOrReplayAsync(
            StatisticReconciliationTrustedRemediationEvidence evidence,
            CancellationToken ct)
    {
        StatisticReconciliationTrustedRemediationEvidenceCanonical
            .RequireValid(evidence);
        await EnsureIndexesAsync(ct);
        try
        {
            await _rows.InsertOneAsync(evidence,
                cancellationToken: ct);
            return (evidence, false);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category ==
                  ServerErrorCategory.DuplicateKey)
        {
            var existing = await _rows.Find(value =>
                    value.Kind ==
                        StatisticReconciliationTrustedRemediationEvidence
                            .RecordKind &&
                    value.Id == evidence.Id)
                .FirstOrDefaultAsync(ct);
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .RequireValid(existing);
            if (!StatisticReconciliationTrustedRemediationEvidenceCanonical
                    .SameTarget(existing!, evidence))
                throw new InvalidOperationException(
                    "P10_REMEDIATION_EVIDENCE_REPLAY_MISMATCH");
            // Authorization actor/time/activation evidence remain the bytes
            // of the first durable authorization. Exact target retries never
            // replace or reinterpret that append-only record.
            return (existing, true);
        }
    }

    internal async Task<
        StatisticReconciliationTrustedRemediationEvidence?> ResolveAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationReview baseVerdict,
            StatisticReconciliationRecheckCaptureBinding successor,
            WorkReportStatisticRebuildJob successorP9,
            CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        var candidates = await _rows.Find(value =>
                value.Kind ==
                    StatisticReconciliationTrustedRemediationEvidence
                        .RecordKind &&
                value.ReconciliationId == run.Id &&
                value.BaseVerdictGenerationId ==
                    baseVerdict.VerdictGenerationId &&
                value.BaseActualGenerationId == run.CurrentGenerationId &&
                value.SuccessorP9GenerationId ==
                    successor.P9GenerationId)
            .Limit(2)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return null;
        if (candidates.Count != 1)
            throw new InvalidOperationException(
                "P10_REMEDIATION_EVIDENCE_CARDINALITY_INVALID");
        var evidence = candidates[0];
        if (!StatisticReconciliationTrustedRemediationEvidenceCanonical
                .Matches(evidence, run, baseVerdict, successor, successorP9))
            throw new InvalidOperationException(
                "P10_REMEDIATION_EVIDENCE_BINDING_INVALID");
        return evidence;
    }

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (_indexesReady)
            return;
        await _indexGate.WaitAsync(ct);
        try
        {
            if (_indexesReady)
                return;
            var keys = Builders<
                StatisticReconciliationTrustedRemediationEvidence>.IndexKeys
                .Ascending(value => value.ReconciliationId)
                .Ascending(value => value.BaseVerdictGenerationId)
                .Ascending(value => value.SuccessorP9GenerationId);
            await _rows.Indexes.CreateOneAsync(
                new CreateIndexModel<
                    StatisticReconciliationTrustedRemediationEvidence>(
                    keys,
                    new CreateIndexOptions<
                        StatisticReconciliationTrustedRemediationEvidence>
                    {
                        Name = "ux_p10Remediation_base_successor_v1",
                        Unique = true,
                        PartialFilterExpression = Builders<
                            StatisticReconciliationTrustedRemediationEvidence>
                            .Filter.Eq(value => value.Kind,
                                StatisticReconciliationTrustedRemediationEvidence
                                    .RecordKind)
                    }),
                cancellationToken: ct);
            _indexesReady = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }
}
