using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.AggregateMapping.Persistence;

// Uses the existing snapshot/majority transaction runner; never falls back to partial writes.
internal sealed class AggregateMongoStore(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner transactions,
    IWorkReportPayloadReader payloadReader, IWorkReportPayloadWriter payloadWriter) : IAggregateTransactionStore
{
    public Task<T> ExecuteAsync<T>(Func<IAggregateTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
        => transactions.ExecuteAsync((session, token) => action(new AggregateMongoTransaction(db, session, payloadReader, payloadWriter), token), ct);

    // Deployment entry only. Not called by Program, controllers, reads or tests in P03.
    internal async Task CreateIndexesAsync(CancellationToken ct)
    {
        foreach (var collection in AggregateCollections.All)
        {
            var indexes = new[]
            {
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("workId").Ascending("target"), new() { Name = "aggregate_scope_target" }),
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("workId").Ascending("keys"), new() { Name = "aggregate_scope_dependency" })
            };
            // Logical uniqueness is the deterministic _id (config/revision/target/owner/receipt).
            await db.Db.GetCollection<BsonDocument>(collection).Indexes.CreateManyAsync(indexes, ct);
        }
    }
}

internal sealed partial class AggregateMongoTransaction(MongoDbContext db, IClientSessionHandle session,
    IWorkReportPayloadReader payloadReader, IWorkReportPayloadWriter payloadWriter) : IAggregateTransaction
{
    public async Task<AggregateStored<T>?> GetAsync<T>(string collection, string id, CancellationToken ct)
    {
        var row = await Collection(collection).Find(session, new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);
        return row == null ? null : Read<T>(row);
    }
    public async Task<IReadOnlyList<AggregateStored<T>>> QueryAsync<T>(string collection, AggregateQuery query, CancellationToken ct)
    {
        var filter = new BsonDocument("workId", query.WorkId);
        if (query.Target != null) filter.Add("target", query.Target);
        if (query.DependencyKey != null) filter.Add("keys", query.DependencyKey);
        var rows = await Collection(collection).Find(session, filter).Sort(new BsonDocument("_id", 1)).Limit(1001).ToListAsync(ct);
        if (rows.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        return rows.Select(Read<T>).ToArray();
    }
    public async Task PutAsync<T>(string collection, string id, long expectedVersion, T value, string workId,
        string? target, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(value, AggregateCanonical.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > 8_388_608) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        var row = new BsonDocument { { "_id", id }, { "version", checked(expectedVersion + 1) }, { "workId", workId },
            { "target", target == null ? BsonNull.Value : new BsonString(target) }, { "keys", new BsonArray(keys) }, { "body", body } };
        if (expectedVersion == 0)
        {
            try { await Collection(collection).InsertOneAsync(session, row, cancellationToken: ct); }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            { throw new AggregatePreviewException("AGG_REVISION_CONFLICT"); }
        }
        else
        {
            var result = await Collection(collection).ReplaceOneAsync(session, new BsonDocument { { "_id", id }, { "version", expectedVersion } }, row, cancellationToken: ct);
            if (result.ModifiedCount != 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        }
        if (expectedVersion > 0 && value is AggregateInstanceState instance)
            await AggregatePreviewInvalidation.CancelSuperseded(db, session, workId, ct, instanceId: instance.Id);
        if (expectedVersion > 0 && value is AggregateConfigHead head)
            await AggregatePreviewInvalidation.CancelSuperseded(db, session, workId, ct, configId: head.Id);
    }
    public async Task DeleteAsync(string collection, string id, long expectedVersion, CancellationToken ct)
    {
        var result = await Collection(collection).DeleteOneAsync(session, new BsonDocument { { "_id", id }, { "version", expectedVersion } }, cancellationToken: ct);
        if (result.DeletedCount != 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
    }
    public async Task FenceAsync(AggregateCommitAuthority authority, IReadOnlyList<AggregateSourcePinDto> sources,
        IReadOnlyList<AggregateCoverageSlotDto> slots, CancellationToken ct)
    {
        if (authority.Read.Context.View == null && authority.Read.Authority.MutationScopeOpen)
        {
            // Re-evaluate the reopened period inside the write transaction, not merely
            // against a capability sampled before its authority pins were collected.
            var a = await db.WorkAssignments.Find(session, x => x.Id == authority.Read.Context.AssignmentId).FirstOrDefaultAsync(ct);
            if (a == null || !await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.IsOpenAsync(
                db, a, authority.Read.Context.WorkReportPeriodId, ct, session)) throw new AggregatePreviewException("AGG_INPUT_STALE");
            if (authority.Read.Authority.ConfigMutationScopeOpen == true &&
                !await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.IsOpenAsync(db, a, null, ct, session))
                throw new AggregatePreviewException("AGG_INPUT_STALE");
        }
        foreach (var pin in authority.Pins.OrderBy(p => p.Collection, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var collection = db.Db.GetCollection<BsonDocument>(pin.Collection);
            var filter = new BsonDocument("_id", AggregateCollections.All.Contains(pin.Collection) ? new BsonString(pin.Id) : new BsonObjectId(ObjectId.Parse(pin.Id)));
            var current = await collection.Find(session, filter).FirstOrDefaultAsync(ct);
            if (current == null || AggregateMongoCommandReader.Fingerprint(current) != pin.Fingerprint) throw new AggregatePreviewException("AGG_INPUT_STALE");
            await collection.UpdateOneAsync(session, filter, new BsonDocument("$inc", new BsonDocument("aggregateCommitFence", 1L)), cancellationToken: ct);
        }
        foreach (var source in sources.DistinctBy(s => s.ReportId).OrderBy(s => s.ReportId, StringComparer.Ordinal))
        {
            var result = await db.WorkAssignmentReports.UpdateOneAsync(session,
                r => r.Id == source.ReportId && r.WorkId == source.WorkId && r.WorkAssignmentId == source.AssignmentId
                    && r.IsCurrent && !r.IsDeleted && r.PayloadRevision == source.PayloadRevision && r.LifecycleRevision == source.LifecycleRevision
                    && r.PayloadHash == source.PayloadHash && r.DynamicFormSchemaHash == source.SchemaHash,
                Builders<WorkAssignmentReport>.Update.Inc("aggregateCommitFence", 1L), cancellationToken: ct);
            if (result.MatchedCount != 1) throw new AggregatePreviewException("AGG_INPUT_STALE");
        }
        foreach (var slot in slots.OrderBy(s => s.SlotKey, StringComparer.Ordinal))
        {
            if (slot.WorkReportPeriodId != null)
            {
                var result = await db.WorkReportPeriods.UpdateOneAsync(session,
                    p => p.Id == slot.WorkReportPeriodId && p.WorkTemplateAssigneeId == slot.BindingId && !p.IsDeleted && p.CurrentReportId == slot.ReportId,
                    Builders<WorkReportPeriod>.Update.Inc("aggregateCommitFence", 1L), cancellationToken: ct);
                if (result.MatchedCount != 1) throw new AggregatePreviewException("AGG_INPUT_STALE");
            }
        }
    }
    public Task<long> WriteTargetAsync(AggregateCommitAuthority authority, AggregateTargetWrite write, CancellationToken ct)
        => write.Instance.Context.View != null ? WriteViewAsync(authority, write, ct) : new AggregateNativeTargetWriter(db, session, payloadReader, payloadWriter).WriteAsync(authority, write, ct);
    private IMongoCollection<BsonDocument> Collection(string name)
    {
        if (!session.IsInTransaction || !AggregateCollections.All.Contains(name)) throw new InvalidOperationException("AGG_TRANSACTION_REQUIRED");
        return db.Db.GetCollection<BsonDocument>(name);
    }
    internal static AggregateStored<T> Read<T>(BsonDocument row)
        => new(row["version"].ToInt64(), JsonSerializer.Deserialize<T>(row["body"].AsString, AggregateCanonical.Json)
            ?? throw new AggregatePreviewException("AGG_STORED_CONTRACT_INVALID"));
}
