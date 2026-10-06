using Hangfire;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregateMaterializationDispatch
{
    // Called by the existing lifecycle dispatcher and after a save. The DB intent
    // survives a queue outage; the worker's lease, not queue delivery, owns publication.
    internal static async Task<int> Schedule(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner transactions,
        IBackgroundJobClient? jobs, int limit, CancellationToken ct, string? workId = null)
    {
        if (jobs == null) return 0;
        var filter = new BsonDocument("keys", new BsonDocument("$in", new BsonArray { "PENDING", "RUNNING", "ASYNC_PENDING", "ASYNC_RUNNING" }));
        if (workId != null) filter.Add("workId", workId);
        var rows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Refresh).Find(filter)
            .Sort(new BsonDocument("_id", 1)).Limit(1000).ToListAsync(ct);
        var payload = new WorkReportPayloadService(db);
        var store = new AggregateMongoStore(db, transactions, payload, payload);
        var dispatched = 0;
        foreach (var row in rows)
        {
            if (dispatched >= Math.Clamp(limit, 1, 100)) break;
            var id = row["_id"].AsString;
            var intent = await store.ExecuteAsync(async (tx, token) => {
                var current = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
                if (current == null || current.Value.DispatchUntil > DateTimeOffset.UtcNow || current.Value.RetryNotBefore > DateTimeOffset.UtcNow
                    || !(current.Value.State == "PENDING" || current.Value.State == "RUNNING" && current.Value.LeaseUntil < DateTimeOffset.UtcNow)) return null;
                await tx.PutAsync(AggregateCollections.Refresh, id, current.Version,
                    current.Value with { DispatchUntil = DateTimeOffset.UtcNow.AddMinutes(2) }, row["workId"].AsString,
                    row["target"].IsString ? row["target"].AsString : null, row["keys"].AsBsonArray.Select(k => k.AsString).ToArray(), token);
                return current.Value;
            }, ct);
            if (intent == null) continue;
            try { jobs.Enqueue<AggregateMaterializationWorker>(worker => worker.Run(intent.InstanceId, intent.Generation, CancellationToken.None)); dispatched++; }
            catch { /* reservation expires; the durable dispatcher retries without losing the save */ }
        }
        return dispatched;
    }
}

public sealed class AggregateMaterializationWorker(MongoDbContext db, IWorkReportPayloadReader payloads,
    IWorkReportPayloadWriter writer, IDynamicFlowDefinitionTransactionRunner transactions, IConfiguration configuration)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(string instanceId, long generation, CancellationToken ct)
    {
        if (!AggregateIntegrationGate.V2Ready(configuration)) return;
        var store = new AggregateMongoStore(db, transactions, payloads, writer);
        AggregateRefreshService? service = null;
        var reader = new AggregateMongoCommandReader(db, payloads, true,
            progress => service!.ProgressAsync(instanceId, generation, progress, ct));
        service = new AggregateRefreshService(store, reader, materialized: true, canRetryRolledBackTransaction: CanRetryRolledBackTransaction);
        var result = await service.RunAsync(instanceId, generation, ct);
        var pending = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh,
            AggregateRefreshService.IntentKey(instanceId, generation), token), ct);
        // Source races can invalidate a lease while it is computing. Resume promptly,
        // but cap immediate retries; durable recurring dispatch handles persistent contention.
        if (pending?.Value.State == "PENDING" && pending.Value.RetryNotBefore is {} retryAt)
        {
            // Persisted retry time is checked by both dispatch and claim. If scheduling
            // fails, the existing durable dispatcher can deliver it after that time.
            try { new BackgroundJobClient().Schedule<AggregateMaterializationWorker>(
                worker => worker.Run(instanceId, generation, CancellationToken.None), retryAt); }
            catch { /* the DB intent remains pending; do not lose it to a queue outage */ }
        }
        else if (pending?.Value.State == "PENDING" && pending.Value.Attempt < 3)
        {
            var current = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token), ct);
            if (current != null) await AggregateMaterializationDispatch.Schedule(db, transactions, new BackgroundJobClient(), 100, ct, current.Value.Context.WorkId);
        }
        if (result == "COMPLETED")
        {
            var instance = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token), ct);
            if (instance != null)
                await AggregateContentSnapshotJob.Schedule(db, new BackgroundJobClient(), AggregateTargetIdentity.Id(instance.Value.Context)!, ct);
        }
    }
    internal static bool CanRetryRolledBackTransaction(Exception exception)
        => !DynamicFlowDefinitionTransactionRunner.IsUnknownTransactionCommitResult(exception)
            && (exception is MongoException && DynamicFlowDefinitionTransactionRunner.IsTransientTransactionFailure(exception)
                || exception.InnerException != null && CanRetryRolledBackTransaction(exception.InnerException));
}
