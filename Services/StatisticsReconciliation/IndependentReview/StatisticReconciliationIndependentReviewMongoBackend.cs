using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

/// <summary>
/// Append-only production owner for P10 review decisions. It deliberately uses a
/// distinct record kinds in the frozen P10 review ledger so gate signatures cannot
/// alias or overwrite FINAL_VERDICT rows.
/// </summary>
public sealed class StatisticReconciliationIndependentReviewMongoBackend(
    MongoDbContext context) : IStatisticReconciliationIndependentReviewBackend
{
    public const string CollectionName =
        "work_report_statistic_reconciliation_reviews";
    private const int MaxTransientTransactionAttempts = 5;
    private const int TransientRetryBaseDelayMilliseconds = 100;

    private static readonly TransactionOptions ReviewTransactionOptions = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    private readonly IMongoCollection<StatisticReconciliationIndependentReviewAuditRecord> _rows =
        context.Db.GetCollection<StatisticReconciliationIndependentReviewAuditRecord>(CollectionName);
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesReady;

    public async Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadLineageAsync(string reconciliationId, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        return await _rows.Find(Builders<StatisticReconciliationIndependentReviewAuditRecord>
                .Filter.And(
                    Builders<StatisticReconciliationIndependentReviewAuditRecord>.Filter.Eq(
                        value => value.ReconciliationId, reconciliationId),
                    ReviewRowsFilter()))
            .SortBy(value => value.CreatedAtUtc).ThenBy(value => value.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>>
        ReadOperationAsync(string operationCommandId, CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        return await _rows.Find(Builders<StatisticReconciliationIndependentReviewAuditRecord>
                .Filter.And(
                    Builders<StatisticReconciliationIndependentReviewAuditRecord>.Filter.Eq(
                        value => value.OperationCommandId, operationCommandId),
                    ReviewRowsFilter()))
            .SortBy(value => value.Gate).ThenBy(value => value.Id).ToListAsync(ct);
    }

    public async Task<StatisticReconciliationReviewAppendOutcome> TryAppendDecisionAsync(
        StatisticReconciliationIndependentReviewAuditRecord record,
        CancellationToken ct = default)
    {
        for (var attempt = 1;
             attempt <= MaxTransientTransactionAttempts;
             attempt++)
        {
            try
            {
                return await TryAppendDecisionOnceAsync(record, ct);
            }
            catch (Exception exception) when (
                attempt < MaxTransientTransactionAttempts &&
                !IsUnknownTransactionCommitResult(exception) &&
                IsTransientTransactionFailure(exception) &&
                !ct.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        TransientRetryBaseDelayMilliseconds *
                        (1 << (attempt - 1))),
                    ct);
            }
        }

        throw new InvalidOperationException(
            "P10_REVIEW_TRANSACTION_RETRY_EXHAUSTED");
    }

    private async Task<StatisticReconciliationReviewAppendOutcome>
        TryAppendDecisionOnceAsync(
        StatisticReconciliationIndependentReviewAuditRecord record,
        CancellationToken ct = default)
    {
        if (record.RecordKind !=
            StatisticReconciliationReviewRecordKinds.Decision)
            throw new ArgumentException("reviewDecisionRecordKind",
                nameof(record));
        await EnsureIndexesAsync(ct);

        using var session = await context.Db.Client.StartSessionAsync(
            cancellationToken: ct);
        session.StartTransaction(ReviewTransactionOptions);
        try
        {
            var run = await context.StatisticReconciliationRuns
                .Find(session, value =>
                    value.Id == record.ReconciliationId &&
                    !value.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (run is null ||
                run.StateRevision != record.StateRevision ||
                run.CurrentGenerationId is null ||
                run.CurrentGenerationHash is null)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }
            StatisticReconciliationRunService
                .RequireActualCaptureReadIntegrity(run);

            var verdicts = await context.StatisticReconciliationReviews
                .Find(session, value =>
                    value.ReconciliationId == run.Id &&
                    value.VerdictGenerationId == record.GenerationId &&
                    value.ActualGenerationId == run.CurrentGenerationId &&
                    value.ActualGenerationSha256 ==
                        run.CurrentGenerationHash &&
                    value.RecordKind ==
                        StatisticReconciliationReviewKinds.FinalVerdict)
                .Limit(2)
                .ToListAsync(ct);
            if (verdicts.Count != 1 ||
                verdicts[0].VerdictGenerationSha256 !=
                    record.GenerationSha256 ||
                verdicts[0].DocumentSemanticSha256 !=
                    record.SemanticVerdictSha256)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }

            var nextStateRevision = checked(run.StateRevision + 1);
            var nextDecisionRevision = checked(
                run.ReviewDecisionRevision + 1);
            var nextStateHash = StatisticReconciliationRunService
                .BuildTrustedReviewDecisionStateHash(
                    run, nextStateRevision, nextDecisionRevision);
            var fb = Builders<StatisticReconciliationRun>.Filter;
            var update = await context.StatisticReconciliationRuns
                .UpdateOneAsync(
                    session,
                    fb.Eq(value => value.Id, run.Id) &
                    fb.Eq(value => value.StateRevision,
                        run.StateRevision) &
                    fb.Eq(value => value.StateHash, run.StateHash) &
                    fb.Eq(value => value.ReviewDecisionRevision,
                        run.ReviewDecisionRevision) &
                    fb.Eq(value => value.CurrentGenerationId,
                        run.CurrentGenerationId) &
                    fb.Eq(value => value.CurrentGenerationHash,
                        run.CurrentGenerationHash) &
                    fb.Eq(value => value.IsDeleted, false),
                    Builders<StatisticReconciliationRun>.Update
                        .Set(value => value.StateRevision,
                            nextStateRevision)
                        .Set(value => value.StateHash, nextStateHash)
                        .Set(value => value.ReviewDecisionRevision,
                            nextDecisionRevision)
                        .Set(value => value.UpdatedAtUtc,
                            record.CreatedAtUtc)
                        .Set(value => value.UpdatedByUserId,
                            record.ReviewerActorId),
                    cancellationToken: ct);
            if (update.ModifiedCount != 1)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }

            await _rows.InsertOneAsync(session, record,
                cancellationToken: ct);
            await CommitWithRetryAsync(session, ct);
            return StatisticReconciliationReviewAppendOutcome.Appended;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category ==
                  ServerErrorCategory.DuplicateKey)
        {
            await session.AbortTransactionAsync(ct);
            var sameOperation = await _rows.Find(value =>
                    value.RecordKind ==
                        StatisticReconciliationReviewRecordKinds.Decision &&
                    value.OperationCommandId == record.OperationCommandId)
                .AnyAsync(ct);
            return sameOperation
                ? StatisticReconciliationReviewAppendOutcome.DuplicateId
                : StatisticReconciliationReviewAppendOutcome.DuplicateGate;
        }
        catch (Exception exception)
        {
            if (!IsUnknownTransactionCommitResult(exception) &&
                session.IsInTransaction)
            {
                await AbortQuietlyAsync(session);
            }
            throw;
        }
    }
    public async Task<StatisticReconciliationReviewAppendOutcome>
        TryAppendSupersessionsAsync(
            IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>
                records,
            CancellationToken ct = default)
    {
        await EnsureIndexesAsync(ct);
        if (records.Count == 0)
            return StatisticReconciliationReviewAppendOutcome.Appended;

        var first = records[0];
        if (records.Any(value =>
                value.RecordKind !=
                    StatisticReconciliationReviewRecordKinds.Supersession ||
                value.ReconciliationId != first.ReconciliationId ||
                value.OperationCommandId != first.OperationCommandId ||
                value.StateRevision != first.StateRevision ||
                value.SupersededByGenerationId is null ||
                value.SupersededByGenerationId !=
                    first.SupersededByGenerationId ||
                value.SupersedesDecisionId is null) ||
            records.Select(value => value.Id)
                .Distinct(StringComparer.Ordinal).Count() != records.Count)
            return StatisticReconciliationReviewAppendOutcome.DuplicateId;

        using var session = await context.Db.Client.StartSessionAsync(
            cancellationToken: ct);
        session.StartTransaction();
        try
        {
            var run = await context.StatisticReconciliationRuns
                .Find(session, value =>
                    value.Id == first.ReconciliationId &&
                    !value.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (run is null ||
                run.StateRevision != first.StateRevision ||
                run.CurrentGenerationId is null ||
                run.CurrentGenerationHash is null)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }
            StatisticReconciliationRunService
                .RequireActualCaptureReadIntegrity(run);

            var successorVerdicts = await context
                .StatisticReconciliationReviews
                .Find(session, value =>
                    value.ReconciliationId == run.Id &&
                    value.VerdictGenerationId ==
                        first.SupersededByGenerationId &&
                    value.ActualGenerationId ==
                        run.CurrentGenerationId &&
                    value.ActualGenerationSha256 ==
                        run.CurrentGenerationHash &&
                    value.RecordKind ==
                        StatisticReconciliationReviewKinds.FinalVerdict)
                .Limit(2)
                .ToListAsync(ct);
            if (successorVerdicts.Count != 1)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(
                successorVerdicts[0]);
            foreach (var record in records)
                StatisticReconciliationIndependentReviewService
                    .ValidateStored(record);

            var sourceDecisionIds = records
                .Select(value => value.SupersedesDecisionId!)
                .ToArray();
            var sourceDecisions = await _rows.Find(session,
                    Builders<StatisticReconciliationIndependentReviewAuditRecord>
                        .Filter.In(value => value.Id, sourceDecisionIds) &
                    Builders<StatisticReconciliationIndependentReviewAuditRecord>
                        .Filter.Eq(value => value.RecordKind,
                            StatisticReconciliationReviewRecordKinds.Decision))
                .ToListAsync(ct);
            if (sourceDecisions.Count != records.Count)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }
            var sourceById = sourceDecisions.ToDictionary(
                value => value.Id, StringComparer.Ordinal);
            foreach (var record in records)
            {
                var source = sourceById[record.SupersedesDecisionId!];
                StatisticReconciliationIndependentReviewService
                    .ValidateStored(source);
                if (source.ReconciliationId != record.ReconciliationId ||
                    source.GenerationId != record.GenerationId ||
                    source.GenerationSha256 != record.GenerationSha256 ||
                    source.SemanticVerdictSha256 !=
                        record.SemanticVerdictSha256 ||
                    source.ReviewRecordSha256 !=
                        record.ReviewRecordSha256 ||
                    source.Gate != record.Gate ||
                    source.Decision !=
                        StatisticReconciliationReviewDecisions.Approve)
                {
                    await session.AbortTransactionAsync(ct);
                    return StatisticReconciliationReviewAppendOutcome
                        .StateFenceLost;
                }
            }

            var nextStateRevision = checked(run.StateRevision + 1);
            var nextReviewDecisionRevision = checked(
                run.ReviewDecisionRevision + 1);
            var nextStateHash = StatisticReconciliationRunService
                .BuildTrustedReviewDecisionStateHash(
                    run, nextStateRevision,
                    nextReviewDecisionRevision);
            var fb = Builders<StatisticReconciliationRun>.Filter;
            var update = await context.StatisticReconciliationRuns
                .UpdateOneAsync(
                    session,
                    fb.Eq(value => value.Id, run.Id) &
                    fb.Eq(value => value.StateRevision,
                        run.StateRevision) &
                    fb.Eq(value => value.StateHash, run.StateHash) &
                    fb.Eq(value => value.ReviewDecisionRevision,
                        run.ReviewDecisionRevision) &
                    fb.Eq(value => value.CurrentGenerationId,
                        run.CurrentGenerationId) &
                    fb.Eq(value => value.CurrentGenerationHash,
                        run.CurrentGenerationHash) &
                    fb.Eq(value => value.IsDeleted, false),
                    Builders<StatisticReconciliationRun>.Update
                        .Set(value => value.StateRevision,
                            nextStateRevision)
                        .Set(value => value.StateHash, nextStateHash)
                        .Set(value => value.ReviewDecisionRevision,
                            nextReviewDecisionRevision)
                        .Set(value => value.UpdatedAtUtc,
                            first.CreatedAtUtc)
                        .Set(value => value.UpdatedByUserId,
                            first.ReviewerActorId),
                    cancellationToken: ct);
            if (update.ModifiedCount != 1)
            {
                await session.AbortTransactionAsync(ct);
                return StatisticReconciliationReviewAppendOutcome
                    .StateFenceLost;
            }

            await _rows.InsertManyAsync(session, records,
                new InsertManyOptions { IsOrdered = true }, ct);
            await session.CommitTransactionAsync(ct);
            return StatisticReconciliationReviewAppendOutcome.Appended;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category ==
                  ServerErrorCategory.DuplicateKey)
        {
            await session.AbortTransactionAsync(ct);
            return StatisticReconciliationReviewAppendOutcome.DuplicateId;
        }
        catch (MongoBulkWriteException<
                   StatisticReconciliationIndependentReviewAuditRecord>
               exception)
            when (exception.WriteErrors.Any(value =>
                value.Category == ServerErrorCategory.DuplicateKey))
        {
            await session.AbortTransactionAsync(ct);
            return StatisticReconciliationReviewAppendOutcome.DuplicateId;
        }
    }
    private static async Task CommitWithRetryAsync(
        IClientSessionHandle session,
        CancellationToken ct)
    {
        for (var attempt = 1;
             attempt <= MaxTransientTransactionAttempts;
             attempt++)
        {
            try
            {
                await session.CommitTransactionAsync(ct);
                return;
            }
            catch (Exception exception) when (
                attempt < MaxTransientTransactionAttempts &&
                IsUnknownTransactionCommitResult(exception) &&
                !ct.IsCancellationRequested)
            {
                // Unknown commit results are retried on this same session.
            }
        }
    }

    private static async Task AbortQuietlyAsync(
        IClientSessionHandle session)
    {
        try
        {
            await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original failure; disposing the fresh session ends it.
        }
    }

    private static bool IsTransientTransactionFailure(Exception exception)
    {
        if (exception is MongoException mongoException &&
            mongoException.HasErrorLabel("TransientTransactionError"))
        {
            return true;
        }
        if (exception is MongoCommandException commandException &&
            commandException.Code is 112 or 244 or 251)
        {
            return true;
        }
        if (exception is MongoWriteException writeException &&
            writeException.WriteError?.Code is 112 or 244 or 251)
        {
            return true;
        }
        if (exception.Message.Contains(
                "Please retry your operation or multi-document transaction",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return exception.InnerException is not null &&
               IsTransientTransactionFailure(exception.InnerException);
    }

    private static bool IsUnknownTransactionCommitResult(
        Exception exception)
    {
        if (exception is MongoException mongoException &&
            mongoException.HasErrorLabel("UnknownTransactionCommitResult"))
        {
            return true;
        }
        return exception.InnerException is not null &&
               IsUnknownTransactionCommitResult(exception.InnerException);
    }
    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (_indexesReady) return;
        await _indexGate.WaitAsync(ct);
        try
        {
            if (_indexesReady) return;
            await _rows.Indexes.CreateManyAsync(BuildIndexModels(), ct);
            _indexesReady = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }
    internal static BsonDocument BuildReviewRowsPredicate() => new(
        "recordKind",
        new BsonDocument("$in", new BsonArray
        {
            StatisticReconciliationReviewRecordKinds.Decision,
            StatisticReconciliationReviewRecordKinds.Supersession
        }));

    internal static bool IsReviewRecordKind(string? value)
        => value is StatisticReconciliationReviewRecordKinds.Decision or
            StatisticReconciliationReviewRecordKinds.Supersession;

    internal static IReadOnlyList<CreateIndexModel<
        StatisticReconciliationIndependentReviewAuditRecord>> BuildIndexModels()
    {
        var decisionFilter =
            Builders<StatisticReconciliationIndependentReviewAuditRecord>.Filter.Eq(
                value => value.RecordKind,
                StatisticReconciliationReviewRecordKinds.Decision);
        var supersessionFilter =
            Builders<StatisticReconciliationIndependentReviewAuditRecord>.Filter.Eq(
                value => value.RecordKind,
                StatisticReconciliationReviewRecordKinds.Supersession);
        return
        [
            new CreateIndexModel<StatisticReconciliationIndependentReviewAuditRecord>(
                Builders<StatisticReconciliationIndependentReviewAuditRecord>.IndexKeys
                    .Ascending(value => value.ReconciliationId)
                    .Ascending(value => value.GenerationId)
                    .Ascending(value => value.Gate),
                new CreateIndexOptions<StatisticReconciliationIndependentReviewAuditRecord>
                {
                    Name = "ux_p10ReviewDecision_generation_gate", Unique = true,
                    PartialFilterExpression = decisionFilter
                }),
            new CreateIndexModel<StatisticReconciliationIndependentReviewAuditRecord>(
                Builders<StatisticReconciliationIndependentReviewAuditRecord>.IndexKeys
                    .Ascending(value => value.OperationCommandId),
                new CreateIndexOptions<StatisticReconciliationIndependentReviewAuditRecord>
                {
                    Name = "ux_p10ReviewDecision_command", Unique = true,
                    PartialFilterExpression = decisionFilter
                }),
            new CreateIndexModel<StatisticReconciliationIndependentReviewAuditRecord>(
                Builders<StatisticReconciliationIndependentReviewAuditRecord>.IndexKeys
                    .Ascending(value => value.SupersedesDecisionId),
                new CreateIndexOptions<StatisticReconciliationIndependentReviewAuditRecord>
                {
                    Name = "ux_p10ReviewSupersession_decision_v2", Unique = true,
                    PartialFilterExpression = supersessionFilter
                }),
            new CreateIndexModel<StatisticReconciliationIndependentReviewAuditRecord>(
                Builders<StatisticReconciliationIndependentReviewAuditRecord>.IndexKeys
                    .Ascending(value => value.ReconciliationId)
                    .Ascending(value => value.CreatedAtUtc)
                    .Ascending(value => value.Id),
                new CreateIndexOptions<StatisticReconciliationIndependentReviewAuditRecord>
                {
                    Name = "ix_p10Review_lineage",
                    PartialFilterExpression = ReviewRowsFilter()
                })
        ];
    }

    private static FilterDefinition<StatisticReconciliationIndependentReviewAuditRecord>
        ReviewRowsFilter()
        => new BsonDocumentFilterDefinition<
            StatisticReconciliationIndependentReviewAuditRecord>(
                BuildReviewRowsPredicate());
}
