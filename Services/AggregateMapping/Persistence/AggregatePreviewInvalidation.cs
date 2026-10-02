using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal static class AggregatePreviewInvalidation
{
    internal static async Task CancelSuperseded(MongoDbContext db, IClientSessionHandle session, string workId,
        CancellationToken ct, string? target = null, string? instanceId = null, string? configId = null)
    {
        // Runs in the very transaction changing the revision. A fresh job cannot
        // observe the new revision before this invalidation has committed.
        var collection = db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection);
        var rows = await collection.Find(session, new BsonDocument { ["workId"] = workId,
            ["state"] = new BsonDocument("$in", new BsonArray { "QUEUED", "RUNNING", "COMPLETED" }) })
            .Project(new BsonDocument { ["request"] = 1, ["target"] = 1, ["stamp"] = 1 }).Limit(1001).ToListAsync(ct);
        if (rows.Count > 1000) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        foreach (var row in rows)
        {
            var request = JsonSerializer.Deserialize<AggregatePreviewJobRequest>(row["request"].AsString, AggregateCanonical.Json)
                ?? throw new AggregatePreviewException("AGG_JOB_REQUEST_INVALID");
            var replaced = (target != null && row["target"] == target)
                || (configId != null && request.ConfigId == configId)
                || (instanceId != null && (request.InstanceId == instanceId || request.Impact?.MigrateInstances.Any(i => i.InstanceId == instanceId) == true));
            if (!replaced) continue;
            await collection.UpdateOneAsync(session, new BsonDocument { ["_id"] = row["_id"], ["stamp"] = row["stamp"] },
                new BsonDocument { ["$set"] = new BsonDocument { ["state"] = "CANCELLED", ["error"] = "AGG_INPUT_STALE", ["updatedAt"] = DateTime.UtcNow },
                    ["$unset"] = new BsonDocument { ["progress"] = "", ["result"] = "", ["lease"] = "" } }, cancellationToken: ct);
        }
    }
}
