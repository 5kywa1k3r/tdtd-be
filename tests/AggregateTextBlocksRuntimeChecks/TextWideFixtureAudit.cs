using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Read-only audit for exactly this harness's fresh 166 x 200 fixtures.
internal static class TextWideFixtureAudit
{
    internal static async Task Inspect(MongoDbContext db,string workId,CancellationToken ct)
    {
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(work.AutoCode,"^r8-text-20261005-[0-9a-f]{8}-wide$"))throw new InvalidOperationException("Only this harness's wide fixture may be inspected.");
        var intents=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Refresh).Find(new BsonDocument("workId",workId)).ToListAsync(ct);
        var instanceRows=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("workId",workId)).ToListAsync(ct);
        var evidence=new{mode="READ_ONLY_OWN_ASYNC_INSPECTION",workId,work.AutoCode,intents=intents.Select(row=>new{keys=row["keys"].ToJson(),body=AggregateMongoTransaction.Read<AggregateRefreshIntent>(row).Value}),instances=instanceRows.Select(row=>{var value=AggregateMongoTransaction.Read<AggregateInstanceState>(row).Value;return new{value.Id,value.Revision,value.Generation,value.AppliedGeneration,value.ComputationProfile,hasApplied=value.Applied!=null};})};
        var json=JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/asj1-inspection-"+workId+".json",json,ct);
    }
    internal static async Task Run(MongoDbContext db,string workId,CancellationToken ct)
    {
        if(!ObjectId.TryParse(workId,out _))throw new InvalidOperationException("Invalid Work ID.");
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(work.AutoCode,"^r8-text-20261005-[0-9a-f]{8}-wide$"))
            throw new InvalidOperationException("Audit accepts only this harness's own wide fixture.");
        var reports=await db.WorkAssignmentReports.Find(r=>r.WorkId==workId&&!r.IsDeleted).ToListAsync(ct);
        var target=reports.Single(r=>r.AssigneeUserId==work.CreatedByUserId);
        var sources=reports.Where(r=>r.Id!=target.Id).ToArray();
        var snapshot=await new WorkReportPayloadService(db).LoadReportPayloadAsync(target,ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(target,snapshot);
        var fields=JsonSerializer.Deserialize<JsonElement>(snapshot.FieldValuesJson!);
        var tables=JsonSerializer.Deserialize<JsonElement>(snapshot.TableValuesJson!).GetProperty("nativeTables").GetProperty("tables");
        var instances=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("workId",workId)).ToListAsync(ct);
        var instance=AggregateMongoTransaction.Read<AggregateInstanceState>(instances.Single()).Value;
        var content=await db.Db.GetCollection<BsonDocument>(AggregateCollections.NativeContent).CountDocumentsAsync(new BsonDocument("workId",workId),cancellationToken:ct);
        var archives=await db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots).CountDocumentsAsync(new BsonDocument("workId",workId),cancellationToken:ct);
        var frozen=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).CountDocumentsAsync(new BsonDocument("workId",workId),cancellationToken:ct);
        var unchanged=target.Status==WorkAssignmentReportStatus.Draft&&target.PayloadRevision==2&&target.LifecycleRevision==1
            &&fields.EnumerateObject().Count()==0&&tables[0].GetProperty("rows").EnumerateArray().All(r=>r.GetProperty("cells").EnumerateObject().Count()==0)
            &&tables.EnumerateArray().Skip(1).All(t=>t.GetProperty("records").GetArrayLength()==0&&!t.TryGetProperty("contentRef",out _))
            &&instance.Revision==1&&instance.Applied==null&&content==0&&archives==0&&frozen==0;
        var evidence=new{mode="READ_ONLY_OWN_WIDE_AUDIT",workId,work.AutoCode,targetId=target.Id,target.Status,target.PayloadRevision,target.LifecycleRevision,
            target.PayloadHash,sourceCount=sources.Length,allCurrentApproved=sources.All(s=>s.IsCurrent&&s.Status==WorkAssignmentReportStatus.Approved),
            instanceId=instance.Id,instance.Revision,hasApplied=instance.Applied!=null,content,archives,frozen,unchanged};
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-wide-audit-"+workId+".json",JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}),ct);
        if(!unchanged||sources.Length!=166||sources.Any(s=>!s.IsCurrent||s.Status!=WorkAssignmentReportStatus.Approved))
            throw new InvalidOperationException("Wide fixture differs from its uncommitted baseline; inspect read-only audit.");
        await FixtureProvenanceChecks.Migration(db,"R8 wide read-only post-failure audit",ct);
        Console.WriteLine("PASS own wide read-only audit: target uncommitted, 166 current Approved sources, no applied mapping/native content/archive/frozen; work="+workId);
    }
}
