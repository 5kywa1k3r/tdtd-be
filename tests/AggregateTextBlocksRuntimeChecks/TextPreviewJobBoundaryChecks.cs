using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextPreviewJobBoundaryChecks
{
    internal static async Task Run(MongoDbContext db,string run,HttpClient http,IConfiguration config,
        string targetId,string sourceId,string instanceId,Action<bool,string> check,CancellationToken ct)
    {
        var payload=new WorkReportPayloadService(db);var rows=db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection);
        var transactions=new DynamicFlowDefinitionTransactionRunner(db,NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var service=new AggregatePreviewJobs(db,payload,config);var local=0;var jobs=new List<string>();
        void Check(bool value,string label){check(value,"P05 job boundary "+label);local++;}
        async Task<JsonElement> Post(string path,object body)
        {
            using var response=await http.PostAsJsonAsync("aggregate-v2/"+path,body,AggregateCanonical.Json,ct);
            var raw=await response.Content.ReadAsStringAsync(ct);
            if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new InvalidOperationException(path+" rejected: "+(int)response.StatusCode+" "+raw);
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        Task<WorkAssignmentReport> Report()=>db.WorkAssignmentReports.Find(r=>r.Id==targetId).SingleAsync(ct);
        async Task<AggregateInstanceState> Instance()=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
            .Find(new BsonDocument("_id",instanceId)).SingleAsync(ct)).Value;
        async Task<AggregatePeriodContextDto> Context()=>(await Post("editor/bootstrap",new{reportId=targetId})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        Task<BsonDocument> Row(string id)=>rows.Find(new BsonDocument("_id",id)).SingleAsync(ct);
        async Task<(string Id,AggregatePeriodContextDto Context,AggregateMappingChangeDto Change)> Start(string suffix)
        {
            var context=await Context();var instance=await Instance();
            // The real Start endpoint retries cancelled artifacts using their
            // stable request identity; no synthetic calculation stamp is added.
            var change=new AggregateMappingChangeDto(instance.Revision,instance.Overrides,instance.Selection,instance.UnlinkedMembers.ToList(),false);
            var request=new AggregatePreviewJobRequest("MAPPING",context,null,instanceId,change);
            var started=await Post("preview-jobs/start",request);var id=started.GetProperty("id").GetString()!;jobs.Add(id);
            Check(started.GetProperty("state").GetString()=="QUEUED","private native host queues preview without writing report "+suffix);
            return(id,context,change);
        }
        var before=await Report();var first=await Start("queued cancel");
        await Post("preview-jobs/"+first.Id+"/cancel",new{});
        await service.Run(first.Id,transactions,payload,ct);
        var cancelled=await Post("preview-jobs/"+first.Id+"/read",new{});
        Check(cancelled.GetProperty("state").GetString()=="CANCELLED"&&!(await Row(first.Id)).Contains("result"),"cancelled queued job cannot publish on delayed worker delivery");
        Check((await Report()).PayloadHash==before.PayloadHash&&(await Report()).PayloadRevision==before.PayloadRevision,"queued cancellation keeps target payload unchanged");

        // Start retries CANCELLED artifacts using the real API and stable identity.
        var second=await Start("running cancel");
        var pause=new PausingReader(payload,sourceId);var pausedService=new AggregatePreviewJobs(db,pause,config);
        var running=pausedService.Run(second.Id,transactions,payload,ct);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);
        var inFlight=await Post("preview-jobs/"+second.Id+"/read",new{});
        Check(inFlight.GetProperty("state").GetString()=="RUNNING","actual worker is running while source I/O is held");
        await Post("preview-jobs/"+second.Id+"/cancel",new{});
        await running.WaitAsync(TimeSpan.FromSeconds(20),ct);
        cancelled=await Post("preview-jobs/"+second.Id+"/read",new{});
        Check(cancelled.GetProperty("state").GetString()=="CANCELLED"&&!(await Row(second.Id)).Contains("result")&&!(await Row(second.Id)).Contains("progress"),
            "cancel during source I/O terminates worker and removes temporary/final values");
        Check((await Report()).PayloadHash==before.PayloadHash,"running cancellation never writes native target");
        var reopened=await Post("preview-jobs/current",new AggregateInstanceReadCommandDto(await Context()));
        Check(reopened.GetProperty("current").ValueKind==JsonValueKind.Null,"reopen excludes cancelled jobs from active preview");

        var recovery=await Start("expired worker lease");var queued=await Row(recovery.Id);
        if(queued["workId"].AsString!=before.WorkId||queued["target"].AsString!=targetId||queued["state"]!="QUEUED")throw new InvalidOperationException("Lease fault injection escaped own fixture.");
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-job-lease-backup-"+run+".json",queued.ToJson(new(){Indent=true}),ct);
        var crashed=queued.DeepClone().AsBsonDocument;crashed["state"]="RUNNING";crashed["lease"]="own-test-interrupted-worker";crashed["leaseUntil"]=DateTime.UtcNow.AddSeconds(-1);
        var replaced=await rows.ReplaceOneAsync(queued,crashed,cancellationToken:ct);
        if(replaced.ModifiedCount!=1)throw new InvalidOperationException("Own queued job changed before lease fault injection.");
        await Task.WhenAll(service.Run(recovery.Id,transactions,payload,ct),service.Run(recovery.Id,transactions,payload,ct));
        var completed=await Post("preview-jobs/"+recovery.Id+"/read",new{});var recovered=await Row(recovery.Id);
        Check(completed.GetProperty("state").GetString()=="COMPLETED"&&recovered["attempt"].ToInt32()==crashed["attempt"].ToInt32()+1,
            "two recovery deliveries acquire one expired lease and publish one preview");
        var stable=recovered.ToJson();await service.Run(recovery.Id,transactions,payload,ct);
        Check((await Row(recovery.Id)).ToJson()==stable,"completed worker delivery is idempotent");
        Check((await Report()).PayloadHash==before.PayloadHash&&(await Report()).PayloadRevision==before.PayloadRevision,"lease recovery produces preview only and preserves native target");

        // Force a different mapping revision via a legitimate Apply, then hold
        // another preview at source I/O while a second legitimate Apply commits.
        async Task Apply(string command,(string Id,AggregatePeriodContextDto Context,AggregateMappingChangeDto Change) entry)
        {
            var preview=await Post("instances/"+instanceId+"/mapping/preview",new AggregateMappingPreviewCommandDto(entry.Context,entry.Change));
            await Post("instances/"+instanceId+"/apply",new AggregateMappingApplyCommandDto(run+command,entry.Context,entry.Change,preview.GetProperty("token").GetString()!));
        }
        await Apply("-job-new-revision",recovery);var changed=await Report();
        var race=await Start("concurrent Apply");pause=new PausingReader(payload,sourceId);pausedService=new AggregatePreviewJobs(db,pause,config);
        running=pausedService.Run(race.Id,transactions,payload,ct);await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);
        await Apply("-job-concurrent-apply",race);var latest=await Report();pause.Release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(20),ct);
        var stale=await Row(race.Id);
        Console.WriteLine("JOB_RACE_OBSERVED state="+stale["state"]+" error="+stale.GetValue("error",BsonNull.Value)+" hasResult="+stale.Contains("result"));
        Check(stale["state"].AsString is "FAILED" or "CANCELLED"&&stale["error"].AsString is "AGG_INPUT_STALE" or "AGG_CONTEXT_STALE" or "AGG_REVISION_CONFLICT"&&!stale.Contains("result")&&!stale.Contains("progress"),
            "preview resumed after concurrent Apply is invalidated/stale and exposes no partial success");
        using(var denied=await http.PostAsJsonAsync("aggregate-v2/preview-jobs/"+race.Id+"/read",new{},AggregateCanonical.Json,ct)){
            var rejection=await denied.Content.ReadAsStringAsync(ct);
            Console.WriteLine("JOB_STALE_READ_OBSERVED status="+(int)denied.StatusCode+" "+rejection);
            Check((int)denied.StatusCode>=400&&(rejection.Contains("AGG_INPUT_STALE",StringComparison.Ordinal)||rejection.Contains("AGG_REVISION_CONFLICT",StringComparison.Ordinal)
                ||rejection.Contains("AGG_CONTEXT_STALE",StringComparison.Ordinal)),"stale job read rejects old scope rather than returning an empty successful preview");
        }
        Check(latest.PayloadRevision==changed.PayloadRevision+1&&(await Report()).PayloadHash==latest.PayloadHash&&(await Report()).PayloadRevision==latest.PayloadRevision,
            "stale worker cannot overwrite the newer authoritative Apply");
        await FixtureProvenanceChecks.Migration(db,"P05 after cancel/lease/race worker",ct);
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-job-boundaries-"+run+".json",JsonSerializer.Serialize(new{run,state="PASS",checks=local,
            targetId,sourceId,instanceId,jobs,scope="REAL_HTTP_AND_REAL_WORKER_WITH_IO_BARRIER_AND_EXPIRED_LEASE_FAULT_INJECTION_NO_BROWSER_OR_PROCESS_KILL"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
    private sealed class PausingReader(IWorkReportPayloadReader inner,string sourceId):IWorkReportPayloadReader
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(WorkAssignmentReport report,CancellationToken ct=default)
        {
            if(report.Id==sourceId){Entered.TrySetResult();await Release.Task.WaitAsync(ct);}
            return await inner.LoadReportPayloadAsync(report,ct);
        }
        public Task<string?> LoadReportTableBlockAsync(WorkAssignmentReport report,string blockId,CancellationToken ct=default)=>inner.LoadReportTableBlockAsync(report,blockId,ct);
        public Task<IReadOnlyDictionary<string,string>> LoadReportTableBlocksAsync(WorkAssignmentReport report,IEnumerable<string> blockIds,CancellationToken ct=default)=>inner.LoadReportTableBlocksAsync(report,blockIds,ct);
    }
}
