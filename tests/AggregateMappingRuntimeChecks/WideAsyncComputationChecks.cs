using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class WideAsyncComputationChecks
{
    internal static async Task Run(MongoDbContext db,IDynamicFlowDefinitionTransactionRunner runner,IConfiguration config,
        RuntimeFixture f,string run,string instanceId,AggregatePeriodContextDto context,AggregateMappingChangeDto change,
        Func<string,object,Task<JsonElement>> call,Action<bool,string> check,CancellationToken ct)
    {
        var timer=Stopwatch.StartNew();
        var before=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);
        var signed=await call($"instances/{instanceId}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change));
        var metadataMs=timer.ElapsedMilliseconds;timer.Restart();
        var command=new AggregateMappingApplyCommandDto(run+"-async-save",context,change,signed.GetProperty("token").GetString()!);
        var saved=await call($"instances/{instanceId}/mapping/queue",command);
        var saveMs=timer.ElapsedMilliseconds; timer.Restart();
        check(saved.GetProperty("state").GetString()=="QUEUED","ASJ1 save returns queued instead of applied");
        check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==before.PayloadRevision,"ASJ1 save leaves target payload unchanged before worker completion");
        var replay=await call($"instances/{instanceId}/mapping/queue",command);
        check(replay.GetProperty("replayed").GetBoolean(),"ASJ1 real HTTP lost-response receipt replay");
        var samples=new List<object>(); JsonElement final=default;var maxReadMs=0L;
        while(true){
            var readTimer=Stopwatch.StartNew();
            var read=await call($"instances/{instanceId}/read",new AggregateInstanceReadCommandDto(context));
            maxReadMs=Math.Max(maxReadMs,readTimer.ElapsedMilliseconds);
            var intent=read.GetProperty("refresh"); var state=intent.GetProperty("state").GetString();
            if(state=="PENDING")await AggregateMaterializationDispatch.Schedule(db,runner,new Hangfire.BackgroundJobClient(),1,ct,f.WorkId);
            if(intent.GetProperty("attempt").GetInt32()>3)throw new InvalidOperationException("Own wide fixture did not stabilize after three worker claims: "+intent.GetProperty("errorCode"));
            if(intent.TryGetProperty("progress",out var progress)&&progress.ValueKind==JsonValueKind.Object)
                samples.Add(new{elapsedMs=timer.ElapsedMilliseconds,processed=progress.GetProperty("processed").GetInt32(),total=progress.GetProperty("total").GetInt32(),bytes=System.Text.Encoding.UTF8.GetByteCount(read.GetRawText())});
            if(state is "COMPLETED" or "FAILED" or "CANCELLED" or "DISCARDED"){final=read;break;}
            await Task.Delay(1000,ct);
        }
        var jobMs=timer.ElapsedMilliseconds;
        Console.WriteLine($"MEASURE ASJ1 sources=166 valuesPerReport=200 sourceValues=33200 textCharsEach={WideContentChecks.Text(1,1).Length} metadataMs={metadataMs} saveMs={saveMs} jobAndPollingMs={jobMs} maxReadMs={maxReadMs} state={final.GetProperty("refresh").GetProperty("state")} error={final.GetProperty("refresh").GetProperty("errorCode")}");
        check(final.GetProperty("refresh").GetProperty("state").GetString()=="COMPLETED","ASJ1 real Hangfire background computation completes");
        var payload=new WorkReportPayloadService(db);var current=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);
        var native=await payload.LoadReportPayloadAsync(current,ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(current,native);
        DynamicFormNativeTableValues.Validate(f.TargetForm,current.DynamicFormSchemaHash,native.TableValuesJson,false);
        const int sum=166*167/2;
        var fields=JsonSerializer.Deserialize<JsonElement>(native.FieldValuesJson!);
        check(Enumerable.Range(1,36).All(i=>fields.GetProperty("n"+i).GetDecimal()==sum*i),"ASJ1 native readback all 36 scalar sums");
        var rows=JsonSerializer.Deserialize<JsonElement>(native.TableValuesJson!).GetProperty("nativeTables").GetProperty("tables")[0].GetProperty("rows");
        check(Enumerable.Range(0,16).All(r=>rows[r].GetProperty("rowId").GetString()=="r"+(r+1)&&Enumerable.Range(1,10).All(c=>rows[r].GetProperty("cells").GetProperty("c"+c).GetProperty("value").GetDecimal()==sum*(40+r*10+c))),"ASJ1 native readback all 160 fixed matrix cells");
        await WideContentChecks.ReadBack(db,f,native.TableValuesJson!,current.PayloadRevision,call,check,ct);
        // Cold saves have no previous derived result. Measure a repeat save
        // against the completed large instance as well, before submission.
        timer.Restart();
        var repeatChange=change with {ExpectedRevision=final.GetProperty("instance").GetProperty("revision").GetInt64()};
        var repeatSigned=await call($"instances/{instanceId}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,repeatChange));
        var repeatMetadataMs=timer.ElapsedMilliseconds;timer.Restart();
        var repeatSaved=await call($"instances/{instanceId}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-async-repeat-save",context,repeatChange,repeatSigned.GetProperty("token").GetString()!));
        var repeatSaveMs=timer.ElapsedMilliseconds;timer.Restart();
        check(repeatSaved.GetProperty("state").GetString()=="QUEUED"&&(await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==current.PayloadRevision,"ASJ1 repeat save keeps completed payload unchanged until worker publishes");
        while(true){
            var repeat=await call($"instances/{instanceId}/read",new AggregateInstanceReadCommandDto(context));
            var repeatState=repeat.GetProperty("refresh").GetProperty("state").GetString();
            if(repeatState=="PENDING")await AggregateMaterializationDispatch.Schedule(db,runner,new Hangfire.BackgroundJobClient(),1,ct,f.WorkId);
            if(repeatState is "COMPLETED" or "FAILED" or "CANCELLED" or "DISCARDED"){
                check(repeatState=="COMPLETED"&&(await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==current.PayloadRevision+1,"ASJ1 repeat save publishes exactly once through real worker");break;
            }
            await Task.Delay(1000,ct);
        }
        var repeatJobMs=timer.ElapsedMilliseconds;
        Console.WriteLine($"MEASURE ASJ1 repeatMetadataMs={repeatMetadataMs} repeatSaveMs={repeatSaveMs} repeatJobAndPollingMs={repeatJobMs}");
        timer.Restart();
        var submission=await call($"reports/{f.Report.Id}/submission-preview",new AggregateInstanceReadCommandDto(context));
        var submitPreviewMs=timer.ElapsedMilliseconds;timer.Restart();
        await LifecycleChecks.Run(db,runner,config,f,run,instanceId,submission.GetProperty("token").GetString()!,ct,materialized:true);
        var lifecycleMs=timer.ElapsedMilliseconds;
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"asj1-wide-"+run+".json"),JsonSerializer.Serialize(new{run,workId=f.WorkId,reportId=f.Report.Id,instanceId,state="PASS",metadataMs,saveMs,jobMs,maxReadMs,repeatMetadataMs,repeatSaveMs,repeatJobMs,submitPreviewMs,lifecycleAndReturnRefreshMs=lifecycleMs,samples,scope="PRIVATE_HTTP_HANGFIRE_NATIVE_WRITER_166_X_200_LONG_TEXT_LIFECYCLE_SEAM_NOT_BROWSER_UAT"},new JsonSerializerOptions{WriteIndented=true}),ct);
        Console.WriteLine($"MEASURE ASJ1 submitMetadataMs={submitPreviewMs} lifecycleAndReturnRefreshMs={lifecycleMs}");
    }
}
