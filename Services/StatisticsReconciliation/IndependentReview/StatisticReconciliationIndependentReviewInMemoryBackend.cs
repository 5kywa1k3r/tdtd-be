namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public sealed class StatisticReconciliationIndependentReviewInMemoryBackend
    : IStatisticReconciliationIndependentReviewBackend
{
    private readonly object _sync = new();
    private readonly List<StatisticReconciliationIndependentReviewAuditRecord> _records = [];

    public Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>> ReadLineageAsync(
        string reconciliationId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>(
                _records.Where(value => value.ReconciliationId == reconciliationId)
                    .OrderBy(value => value.CreatedAtUtc).ThenBy(value => value.Id).ToArray());
    }

    public Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>> ReadOperationAsync(
        string operationCommandId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>(
                _records.Where(value => value.OperationCommandId == operationCommandId)
                    .OrderBy(value => value.Gate).ToArray());
    }

    public Task<StatisticReconciliationReviewAppendOutcome> TryAppendDecisionAsync(
        StatisticReconciliationIndependentReviewAuditRecord record, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_records.Any(value => value.Id == record.Id ||
                                      value.OperationCommandId == record.OperationCommandId))
                return Task.FromResult(StatisticReconciliationReviewAppendOutcome.DuplicateId);
            if (_records.Any(value => value.RecordKind == StatisticReconciliationReviewRecordKinds.Decision &&
                                      value.ReconciliationId == record.ReconciliationId &&
                                      value.GenerationId == record.GenerationId &&
                                      value.Gate == record.Gate))
                return Task.FromResult(StatisticReconciliationReviewAppendOutcome.DuplicateGate);
            _records.Add(record);
            return Task.FromResult(StatisticReconciliationReviewAppendOutcome.Appended);
        }
    }

    public Task<StatisticReconciliationReviewAppendOutcome> TryAppendSupersessionsAsync(
        IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord> records,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (records.Count == 0)
                return Task.FromResult(StatisticReconciliationReviewAppendOutcome.Appended);
            var operation = records[0].OperationCommandId;
            var existing = _records.Where(value => value.OperationCommandId == operation).ToArray();
            if (existing.Length > 0)
                return Task.FromResult(StatisticReconciliationReviewAppendOutcome.DuplicateId);
            if (records.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != records.Count ||
                records.Any(value => _records.Any(stored => stored.Id == value.Id)) ||
                records.Any(value => value.RecordKind != StatisticReconciliationReviewRecordKinds.Supersession))
                return Task.FromResult(StatisticReconciliationReviewAppendOutcome.DuplicateId);
            _records.AddRange(records);
            return Task.FromResult(StatisticReconciliationReviewAppendOutcome.Appended);
        }
    }
}
