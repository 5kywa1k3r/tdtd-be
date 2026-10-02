using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class ListL4StateRead
{
    internal static async Task Run(string reportId)
    {
        var db=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000",Database="tdtd"}));
        var report=await db.WorkAssignmentReports.Find(r=>r.Id==reportId).SingleAsync();
        var work=await db.Works.Find(w=>w.Id==report.WorkId).SingleAsync();
        if(!work.AutoCode.StartsWith("list-l4-",StringComparison.Ordinal))throw new InvalidOperationException("Read evidence is restricted to isolated L4 fixtures.");
        var filter=new BsonDocument("workId",work.Id);
        var instances=(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(filter).ToListAsync())
            .Select(AggregateMongoTransaction.Read<AggregateInstanceState>).Select(x=>x.Value).Where(x=>x.Context.ReportId==reportId).ToArray();
        var ids=instances.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        var snapshots=(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(filter).ToListAsync())
            .Where(r=>AggregateMongoTransaction.Read<AggregateFrozenState>(r).Value.TargetReportId==reportId)
            .Select(r=>new{Id=r["_id"].AsString,Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r["body"].AsString)))}).OrderBy(r=>r.Id).ToArray();
        var owners=(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks).Find(filter).ToListAsync())
            .Select(AggregateMongoTransaction.Read<AggregateLockState>).SelectMany(x=>x.Value.Owners).Where(x=>ids.Contains(x.InstanceId)).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new{report.Id,report.WorkId,Status=report.Status.ToString(),report.LifecycleRevision,report.PayloadRevision,report.PayloadHash,
            report.LifecycleProjectionLastCompletedRevision,report.LifecycleProjectionLastError,
            Instances=instances.Select(x=>new{x.Id,x.State,x.Revision,x.SubmissionId}),SnapshotDigests=snapshots,LockOwnerCount=owners.Length},new JsonSerializerOptions{WriteIndented=true}));
    }
}
