using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

// Read-only inventory, deliberately no repair, enqueue, payload or frozen-history write.
internal static class CompletionDateImpactAudit
{
    internal static async Task Browser(MongoDbContext db,string workId,CancellationToken ct){
        var work=await db.Works.Find(w=>w.Id==workId).SingleAsync(ct);
        if(!work.AutoCode.StartsWith("r8-text-",StringComparison.Ordinal))throw new InvalidOperationException("Only own R8 fixture.");
        var states=new List<object>();
        using var cursor=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).FindAsync(new BsonDocument("workId",workId),cancellationToken:ct);
        while(await cursor.MoveNextAsync(ct))foreach(var row in cursor.Current){
            var value=AggregateMongoTransaction.Read<AggregateInstanceState>(row).Value;
            if(value.Context.WorkId!=workId)throw new InvalidOperationException("Wrong scope");
            var versions=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Versions).Find(new BsonDocument("body",new BsonRegularExpression(value.ConfigId))).ToListAsync(ct);
            var pinned=versions.Select(v=>AggregateMongoTransaction.Read<AggregateConfigVersion>(v).Value).Single(v=>v.Revision==value.ConfigRevision);
            var report=await db.WorkAssignmentReports.Find(r=>r.Id==value.Context.ReportId).FirstOrDefaultAsync(ct);
            states.Add(new{value.Id,value.Context,value.State,value.Revision,value.ConfigRevision,value.Generation,value.AppliedGeneration,value.Overrides,recipe=pinned.Recipe,report=report==null?null:new{report.Id,report.Status,report.PayloadRevision,report.PayloadHash,report.LifecycleRevision}});
        }
        var json=JsonSerializer.Serialize(new{mode="READ_ONLY",workId,states,writes=0},new JsonSerializerOptions{WriteIndented=true});
        var path=$"../outputs/p05-ui-final-20261006/browser-readback-{workId}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json";
        await File.WriteAllTextAsync(path,json,ct);Console.WriteLine($"READ_ONLY own browser fixture {workId}: {states.Count} instances, no writes. Evidence: {path}");
    }
    internal static async Task Run(MongoDbContext db,CancellationToken ct)
    {
        var versions=new Dictionary<string,bool>(StringComparer.Ordinal);
        using(var cursor=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Versions).FindAsync(FilterDefinition<BsonDocument>.Empty,new FindOptions<BsonDocument>{BatchSize=100},ct))
            while(await cursor.MoveNextAsync(ct))foreach(var row in cursor.Current){
                var version=AggregateMongoTransaction.Read<AggregateConfigVersion>(row).Value;
                versions[version.ConfigId+":"+version.Revision]=JsonSerializer.Serialize(version.Recipe,AggregateCanonical.Json).Contains("ASSIGNMENT_COMPLETED",StringComparison.Ordinal);
            }
        var candidates=new List<object>();var instanceCount=0;var scopedCount=0;
        using(var cursor=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).FindAsync(FilterDefinition<BsonDocument>.Empty,new FindOptions<BsonDocument>{BatchSize=25},ct))
            while(await cursor.MoveNextAsync(ct))foreach(var row in cursor.Current){
                var instance=AggregateMongoTransaction.Read<AggregateInstanceState>(row).Value;instanceCount++;
                var hasFilter=versions.GetValueOrDefault(instance.ConfigId+":"+instance.ConfigRevision)||JsonSerializer.Serialize(instance.Overrides,AggregateCanonical.Json).Contains("ASSIGNMENT_COMPLETED",StringComparison.Ordinal);
                if(!hasFilter)continue;scopedCount++;
                var assignments=await db.WorkAssignments.Find(a=>a.WorkId==instance.Context.WorkId&&!a.IsDeleted).ToListAsync(ct);
                var shifted=assignments.Where(a=>a.CompletedDate.HasValue&&WorkCompletionDate.Read(a.CompletedDate,a.CompletedAtUtc,a.CompletionMode)?.Date!=a.CompletedDate.Value.Date).Select(a=>new{a.Id,a.ParentAssignmentId,storedDay=a.CompletedDate,readDay=WorkCompletionDate.Read(a.CompletedDate,a.CompletedAtUtc,a.CompletionMode),a.CompletionMode}).ToArray();
                if(shifted.Length==0)continue;
                var work=await db.Works.Find(w=>w.Id==instance.Context.WorkId).FirstOrDefaultAsync(ct);
                candidates.Add(new{instance.Id,instance.Context.WorkId,instance.Context.AssignmentId,instance.Context.ReportId,instance.State,instance.ConfigRevision,instance.Generation,instance.AppliedGeneration,
                    fixture=work?.AutoCode?.StartsWith("r8-text-",StringComparison.Ordinal)==true,mayRecompute=instance.State=="DRAFT",scope="conservative same-Work candidates, not proof of changed result",shifted});
            }
        var json=JsonSerializer.Serialize(new{mode="READ_ONLY",instanceCount,scopedCount,candidates,writes=0},new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync("../outputs/p05-ui-final-20261006/completion-date-impact.json",json,ct);Console.WriteLine(json);
    }
}
