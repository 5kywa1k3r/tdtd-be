using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class TextSourceLifecycleChecks
{
    internal static async Task Run(MongoDbContext db,string run,HttpClient http,string prefix,string instanceId,string targetId,
        string sourceId,Action<bool,string> check,CancellationToken ct)
    {
        var evidence=new List<object>();
        async Task<JsonElement> Post(string path,object body){using var response=await http.PostAsJsonAsync(path,body,AggregateCanonical.Json,ct);
            var text=await response.Content.ReadAsStringAsync(ct);if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new Exception(path+" "+response.StatusCode+" "+text);return JsonSerializer.Deserialize<JsonElement>(text);}
        async Task<AggregateInstanceState> Instance()=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceId)).SingleAsync(ct)).Value;
        Task<WorkAssignmentReport> Source()=>db.WorkAssignmentReports.Find(r=>r.Id==sourceId).SingleAsync(ct);
        async Task<AggregatePeriodContextDto> Context()=>(await Post("aggregate-v2/editor/bootstrap",new{reportId=targetId})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        async Task<AggregateInstanceState> Wait(long previousRevision,string label){var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(50)){
                var state=await Instance();var row=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Refresh).Find(new BsonDocument("_id",AggregateRefreshService.IntentKey(instanceId,state.Generation))).SingleAsync(ct);
                var intent=AggregateMongoTransaction.Read<AggregateRefreshIntent>(row).Value;
                if(intent.State=="FAILED")throw new Exception(label+" job failed "+intent.ErrorCode);
                if(intent.State=="COMPLETED"&&state.Revision>previousRevision){evidence.Add(new{label,elapsedMs=watch.ElapsedMilliseconds,state.Revision,state.Generation,intent.Attempt});return state;}
                await Task.Delay(150,ct);
            }throw new Exception(label+" did not complete from immediate queue wake");}
        // Process the normal host's isolated queue. No direct worker invocation and no recurring dispatch.
        var storage=new MongoStorage(db.Db.Client,"tdtd",new MongoStorageOptions{Prefix=prefix,CheckQueuedJobsStrategy=CheckQueuedJobsStrategy.Poll,QueuePollInterval=TimeSpan.FromMilliseconds(100)});
        using var server=new BackgroundJobServer(new BackgroundJobServerOptions{ServerName=run+"-source-status",WorkerCount=1,SchedulePollingInterval=TimeSpan.FromSeconds(1),ShutdownTimeout=TimeSpan.FromSeconds(5)},storage);
        var original=await Instance();var context=await Context();
        var selected=new AggregateInstanceSelectionDto(original.Selection.Sources.Select(s=>s with{Mode="EXPLICIT_REPORTS",ReportIds=[sourceId],ExcludedReportIds=[]}).ToList());
        var change=new AggregateMappingChangeDto(original.Revision,original.Overrides,selected,original.UnlinkedMembers.ToList(),false);
        var token=(await Post($"aggregate-v2/instances/{instanceId}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change))).GetProperty("token").GetString()!;
        await Post($"aggregate-v2/instances/{instanceId}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-explicit-source",context,change,token));
        var active=await Wait(original.Revision,"explicit baseline");
        var statusWatch=Stopwatch.StartNew();
        var statusBefore=await Instance();
        var status=await Post($"aggregate-v2/instances/{instanceId}/computation/status",new AggregateInstanceReadCommandDto(await Context()));
        var statusAfter=await Instance();var statusRevision=status.GetProperty("revision").GetInt64();
        // Post-publication projection may refresh the same generation between these reads.
        // The HTTP snapshot must fit the persisted revision interval, without carrying values.
        check(statusRevision>=statusBefore.Revision&&statusRevision<=statusAfter.Revision
            &&status.GetProperty("generation").GetInt64()==statusBefore.Generation&&statusBefore.Generation==statusAfter.Generation
            &&status.EnumerateObject().Count()==4&&!status.TryGetProperty("lastResults",out _),
            "source-status metadata response omits payload/source/trace; before="+statusBefore.Revision+", after="+statusAfter.Revision+", response="+status.GetRawText());
        active=statusAfter;
        evidence.Add(new{label="status metadata",elapsedMs=statusWatch.ElapsedMilliseconds,bytes=status.GetRawText().Length,beforeRevision=statusBefore.Revision,statusRevision,afterRevision=statusAfter.Revision});
        check(active.Applied!.Preview.ContributingSources.Any(p=>p.ReportId==sourceId),"source-status explicit baseline contributes");
        async Task Mutate(string operation){var source=await Source();var watch=Stopwatch.StartNew();
            await Post("work-assignment-review/reports/"+sourceId+"/"+operation,new ReportActiveRequest{CommandId=run+"-status-"+operation,ExpectedPayloadRevision=source.PayloadRevision,ExpectedLifecycleRevision=source.LifecycleRevision,Comment="Dữ liệu thử riêng: kiểm tính nền khi ẩn/hiện"});
            evidence.Add(new{operation,ackMs=watch.ElapsedMilliseconds});}
        await Mutate("deactivate");
        var hidden=await Wait(active.Revision,"hidden immediate refresh");
        check(hidden.Selection.Sources.All(s=>s.ReportIds.Contains(sourceId))&&hidden.Applied!.Preview.ContributingSources.All(p=>p.ReportId!=sourceId),"source-status hide preserves explicit IDs and removes contribution");
        check(hidden.Applied!.Preview.Issues.Any(i=>i.Code=="AGG_SOURCE_INACTIVE"),"source-status hidden warning available");
        var read=await Post($"aggregate-v2/instances/{instanceId}/read",new AggregateInstanceReadCommandDto(await Context()));
        check(read.GetProperty("resultReadError").ValueKind==JsonValueKind.Null,"source-status fresh empty result is readable after hidden job");
        JsonElement preview=default;string previewId="";
        // Normal post-publication projection may advance target pins after COMPLETED.
        // Treat that as a real stale rejection, then explicitly request a fresh preview.
        for(var trial=0;trial<3;trial++){
            hidden=await Instance();
            var hiddenChange=new AggregateMappingChangeDto(hidden.Revision,hidden.Overrides,hidden.Selection,hidden.UnlinkedMembers.ToList(),false);
            preview=await Post("aggregate-v2/preview-jobs/start",new AggregatePreviewJobRequest("MAPPING",await Context(),null,instanceId,hiddenChange));
            previewId=preview.GetProperty("id").GetString()!;var previewClock=Stopwatch.StartNew();var stale=false;
            while(preview.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){
                if(previewClock.Elapsed>TimeSpan.FromSeconds(30))throw new Exception("Hidden preview timeout");
                await Task.Delay(150,ct);
                using var response=await http.PostAsJsonAsync("aggregate-v2/preview-jobs/"+previewId+"/read",new{},AggregateCanonical.Json,ct);
                var text=await response.Content.ReadAsStringAsync(ct);
                if(response.StatusCode==HttpStatusCode.Conflict&&(text.Contains("AGG_INPUT_STALE")||text.Contains("AGG_REVISION_CONFLICT"))){
                    evidence.Add(new{label="post-publication pin fence",trial,error=text});stale=true;break;
                }
                response.EnsureSuccessStatusCode();preview=JsonSerializer.Deserialize<JsonElement>(text);
            }
            if(!stale)break;
        }
        check(preview.GetProperty("state").GetString()=="COMPLETED","source-status real preview job accepts authorized hidden explicit source");
        await Mutate("reactivate");
        var restored=await Wait(hidden.Revision,"reactivate immediate refresh");
        check(restored.Applied!.Preview.ContributingSources.Any(p=>p.ReportId==sourceId),"source-status reactivation restores current Approved contributor without selection edit");
        using(var stale=await http.PostAsJsonAsync("aggregate-v2/preview-jobs/"+previewId+"/read",new{},AggregateCanonical.Json,ct)){
            var error=await stale.Content.ReadAsStringAsync(ct);
            evidence.Add(new{label="stale preview read",status=(int)stale.StatusCode,error});
            Console.WriteLine("SOURCE_STALE_READ status="+(int)stale.StatusCode+" body="+error);
            check((int)stale.StatusCode>=400&&(error.Contains("AGG_INPUT_STALE")||error.Contains("AGG_PREVIEW_UNAVAILABLE")||error.Contains("AGG_REVISION_CONFLICT")),"source-status reopen rejects preview captured before reactivation");
        }
        var sourceBefore=await Source();
        await Post("work-assignment-review/reports/"+sourceId+"/recall-approved",new ReturnReportRequest{CommandId=run+"-status-recall",ExpectedPayloadRevision=sourceBefore.PayloadRevision,ExpectedLifecycleRevision=sourceBefore.LifecycleRevision,Comment="Kiểm tính lại"});
        var recalled=await Wait(restored.Revision,"recall immediate refresh");
        check(recalled.Applied!.Preview.LinkedSources.Any(p=>p.ReportId==sourceId)&&recalled.Applied.Preview.ContributingSources.All(p=>p.ReportId!=sourceId),"source-status Submitted remains linked but does not contribute or fallback");
        var submitted=await Source();
        await Post("work-assignment-review/reports/"+sourceId+"/approve",new ApproveReportRequest{CommandId=run+"-status-reapprove",ExpectedPayloadRevision=submitted.PayloadRevision,ExpectedLifecycleRevision=submitted.LifecycleRevision,Comment="Phục hồi nguồn thử"});
        var approved=await Wait(recalled.Revision,"approve immediate refresh");
        check(approved.Applied!.Preview.ContributingSources.Any(p=>p.ReportId==sourceId),"source-status Approved contributes again");
        var drain=Stopwatch.StartNew();
        while(storage.GetMonitoringApi().ProcessingCount()>0 || storage.GetMonitoringApi().EnqueuedCount("default")>0){
            if(drain.Elapsed>TimeSpan.FromSeconds(20))throw new Exception("Own source-status queue has not drained");await Task.Delay(150,ct);
        }
        await TextFormulaProbeChecks.Run(db,http,run,targetId,sourceId,check,ct);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"formula-source-"+run+".json"),JsonSerializer.Serialize(new{run,instanceId,targetId,sourceId,evidence,scope="NORMAL_LOGIN_HTTP_REAL_HANGFIRE_NO_RECURRING_NO_DIRECT_WORKER"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
