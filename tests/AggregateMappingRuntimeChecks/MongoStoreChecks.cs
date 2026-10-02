using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class MongoStoreChecks
{
    internal static async Task<IReadOnlyList<string>> Run(MongoDbContext db, string runId, CancellationToken ct)
    {
        var checks = new List<string>();
        var runner = new DynamicFlowDefinitionTransactionRunner(db, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var payload = new WorkReportPayloadService(db);
        var store = new AggregateMongoStore(db, runner, payload, payload);
        var workId = ObjectId.GenerateNewId().ToString();
        var collection = AggregateCollections.Receipts;
        var id = runId + "-receipt";
        var value = new AggregateReceipt("p05-hash", "p05-fixture", "P05_STORAGE_PROBE", JsonSerializer.SerializeToElement(new { runId }));
        void Check(bool passed, string name)
        { if (!passed) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        async Task<long> Count(string rowId) => await db.Db.GetCollection<BsonDocument>(collection).CountDocumentsAsync(new BsonDocument("_id", rowId), cancellationToken: ct);

        Check(await Count(id) == 0, "unique fixture ID absent before write");
        await store.ExecuteAsync(async (tx, token) => { await tx.PutAsync(collection, id, 0, value, workId, runId, [], token); return true; }, ct);
        var row = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateReceipt>(collection, id, token), ct);
        Check(row?.Version == 1 && row.Value.Operation == "P05_STORAGE_PROBE", "real transaction committed version 1");

        var abortedId = runId + "-abort";
        try
        {
            await store.ExecuteAsync<bool>(async (tx, token) => {
                await tx.PutAsync(collection, abortedId, 0, value, workId, runId, [], token);
                await tx.PutAsync(collection, id, 1, value with { Operation = "SHOULD_ABORT" }, workId, runId, [], token);
                throw new InvalidOperationException("P05_INJECTED_ABORT");
            }, ct);
            throw new InvalidOperationException("abort was not propagated");
        }
        catch (InvalidOperationException ex) when (ex.Message == "P05_INJECTED_ABORT") { }
        row = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateReceipt>(collection, id, token), ct);
        Check(await Count(abortedId) == 0 && row?.Version == 1 && row.Value.Operation == "P05_STORAGE_PROBE", "Mongo rollback restores all writes in aborted transaction");

        async Task<bool> Race(string operation)
        {
            try { return await store.ExecuteAsync(async (tx, token) => { await tx.PutAsync(collection, id, 1, value with { Operation = operation }, workId, runId, [], token); return true; }, ct); }
            catch (AggregatePreviewException ex) when (ex.Code == "AGG_REVISION_CONFLICT") { return false; }
        }
        var winners = await Task.WhenAll(Race("P05_RACE_A"), Race("P05_RACE_B"));
        Check(winners.Count(v => v) == 1, "concurrent expected-version writes have one winner");
        row = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateReceipt>(collection, id, token), ct);
        Check(row?.Version == 2, "CAS race increments persisted version once");

        var before = (await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", id)).SingleAsync(ct)).ToJson();
        var queried = await store.ExecuteAsync((tx, token) => tx.QueryAsync<AggregateReceipt>(collection, new(workId, runId), token), ct);
        var after = (await db.Db.GetCollection<BsonDocument>(collection).Find(new BsonDocument("_id", id)).SingleAsync(ct)).ToJson();
        Check(queried.Count == 1 && before == after, "scoped read leaves persisted document unchanged");
        var unrelated = await store.ExecuteAsync((tx, token) => tx.QueryAsync<AggregateReceipt>(collection, new(ObjectId.GenerateNewId().ToString(), runId), token), ct);
        Check(unrelated.Count == 0, "store query is scoped by Work");

        try { await store.ExecuteAsync(async (tx, token) => { await tx.DeleteAsync(collection, id, 1, token); return true; }, ct); throw new InvalidOperationException("stale delete accepted"); }
        catch (AggregatePreviewException ex) when (ex.Code == "AGG_REVISION_CONFLICT") { }
        Check(await Count(id) == 1, "stale delete preserves receipt");
        // Keep the run's single synthetic receipt for inspection. Never drop a collection/database.
        return checks;
    }
}
