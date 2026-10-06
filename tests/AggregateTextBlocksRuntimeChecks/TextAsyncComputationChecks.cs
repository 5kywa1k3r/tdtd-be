using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextAsyncComputationChecks
{
    internal static async Task Run(MongoDbContext db,string run,HttpClient http,IConfiguration config,
        string targetId,string instanceId,Action<bool,string> check,CancellationToken ct)
    {
        var local=0;
        void Check(bool value,string label){check(value,"ASJ1 native "+label);local++;}
        async Task<(HttpStatusCode Status,JsonElement Body)> Send(string path,object body){
            using var response=await http.PostAsJsonAsync(path,body,AggregateCanonical.Json,ct);
            return(response.StatusCode,JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct)));
        }
        async Task<JsonElement> Ok(string path,object body){var response=await Send("aggregate-v2/"+path,body);
            if(response.Status is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new InvalidOperationException(path+" rejected "+response.Status+" "+response.Body);return response.Body;}
        Task<WorkAssignmentReport> Report()=>db.WorkAssignmentReports.Find(r=>r.Id==targetId).SingleAsync(ct);
        async Task<AggregateInstanceState> Instance()=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceId)).SingleAsync(ct)).Value;
        async Task<AggregatePeriodContextDto> Context()=>(await Ok("editor/bootstrap",new{reportId=targetId})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var payload=new WorkReportPayloadService(db);var runner=new DynamicFlowDefinitionTransactionRunner(db,NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var store=new AggregateMongoStore(db,runner,payload,payload);
        var worker=new AggregateMaterializationWorker(db,payload,payload,runner,config);
        async Task Queue(string suffix){
            var state=await Instance();var context=await Context();var before=await Report();
            var change=new AggregateMappingChangeDto(state.Revision,state.Overrides,state.Selection,state.UnlinkedMembers.ToList(),false);
            var token=(await Ok($"instances/{instanceId}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change))).GetProperty("token").GetString()!;
            var command=new AggregateMappingApplyCommandDto(run+suffix,context,change,token);
            var saved=await Ok($"instances/{instanceId}/mapping/queue",command);
            Check(saved.GetProperty("state").GetString()=="QUEUED"&&(await Report()).PayloadRevision==before.PayloadRevision,"save method leaves report value unchanged "+suffix);
            Check((await Ok($"instances/{instanceId}/mapping/queue",command)).GetProperty("replayed").GetBoolean(),"save receipt replay "+suffix);
        }
        await Queue("-async-pending");
        var pending=await Report();
        var rejected=await Send("work-assignment-reports/"+targetId+"/submit",new SubmitWorkAssignmentReportRequest{CommandId=run+"-async-pending-submit",ExpectedPayloadRevision=pending.PayloadRevision,ExpectedLifecycleRevision=pending.LifecycleRevision,LateReason="Fixture ASJ1"});
        Check((int)rejected.Status>=400&&rejected.Body.GetRawText().Contains("AGG_COMPUTATION_REQUIRED")&&(await Report()).Status==WorkAssignmentReportStatus.Draft,"normal Submit rejects pending compute without changing report");
        var state=await Instance();
        await Ok($"instances/{instanceId}/computation/cancel",new AggregateUnlinkPreviewCommandDto(await Context(),state.Revision));
        await worker.Run(instanceId,state.Generation,ct);
        Check((await Report()).PayloadRevision==pending.PayloadRevision,"cancel before claim stops queued publication");
        await Ok($"instances/{instanceId}/computation/retry",new AggregateUnlinkPreviewCommandDto(await Context(),state.Revision));
        await worker.Run(instanceId,state.Generation,ct);
        var complete=await Instance();var beforeSubmit=await Report();
        Check(complete.AppliedGeneration==complete.Generation&&complete.AppliedInputStamp!=null&&beforeSubmit.PayloadRevision==pending.PayloadRevision+1,"retry uses native writer and durable input stamp");
        await worker.Run(instanceId,state.Generation,ct);
        Check((await Report()).PayloadRevision==beforeSubmit.PayloadRevision,"duplicate worker leaves native payload revision unchanged");
        var submission=(await Ok("reports/"+targetId+"/submission-preview",new AggregateInstanceReadCommandDto(await Context()))).GetProperty("token").GetString()!;
        var submitted=await Send("work-assignment-reports/"+targetId+"/submit",new SubmitWorkAssignmentReportRequest{CommandId=run+"-async-submit",ExpectedPayloadRevision=beforeSubmit.PayloadRevision,ExpectedLifecycleRevision=beforeSubmit.LifecycleRevision,AggregateConfirmationToken=submission,LateReason="Fixture ASJ1"});
        Check((submitted.Status is HttpStatusCode.OK or HttpStatusCode.Accepted)&&(await Report()).Status==WorkAssignmentReportStatus.Submitted&&(await Instance()).State=="FROZEN","normal Submit freezes completed materialized result");
        var contextFrozen=await Context();var frozen=await Instance();
        rejected=await Send("aggregate-v2/instances/"+instanceId+"/computation/retry",new AggregateUnlinkPreviewCommandDto(contextFrozen,frozen.Revision));
        Check((int)rejected.Status>=400&&(await Instance()).State=="FROZEN","submitted instance cannot retry or recompute");
        var report=await Report();
        var returned=await Send("work-assignment-reports/"+targetId+"/withdraw-submitted",new ReturnWorkAssignmentReportRequest{CommandId=run+"-async-withdraw",ExpectedPayloadRevision=report.PayloadRevision,ExpectedLifecycleRevision=report.LifecycleRevision,ReturnReason="Fixture ASJ1"});
        Check(returned.Status is HttpStatusCode.OK or HttpStatusCode.Accepted,"normal Withdraw returns report to Draft");
        state=await Instance();await worker.Run(instanceId,state.Generation,ct);
        Check((await Instance()).AppliedGeneration==state.Generation&&(await Report()).Status==WorkAssignmentReportStatus.Draft,"Return generation materializes without modifying historical frozen snapshot");
        var beforeCancel=await Report();await Queue("-async-running-cancel");state=await Instance();
        AggregateRefreshService? refresh=null;var canceled=false;
        var reader=new AggregateMongoCommandReader(db,payload,true,async progress=>{
            await refresh!.ProgressAsync(instanceId,state.Generation,progress,ct);
            if(!canceled){canceled=true;await Ok($"instances/{instanceId}/computation/cancel",new AggregateUnlinkPreviewCommandDto(await Context(),state.Revision));}
        });
        refresh=new AggregateRefreshService(store,reader,true);
        await refresh.RunAsync(instanceId,state.Generation,ct);
        var cancelled=await Ok($"instances/{instanceId}/read",new AggregateInstanceReadCommandDto(await Context()));
        Check(canceled&&cancelled.GetProperty("refresh").GetProperty("state").GetString()=="CANCELLED"&&(await Report()).PayloadRevision==beforeCancel.PayloadRevision,"cancel during real source progress preserves old payload");
        await Ok($"instances/{instanceId}/computation/retry",new AggregateUnlinkPreviewCommandDto(await Context(),state.Revision));await worker.Run(instanceId,state.Generation,ct);
        state=await Instance();
        var version=AggregateMongoTransaction.Read<AggregateConfigVersion>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Versions).Find(new BsonDocument("_id",AggregateCommandService.VersionKey(state.ConfigId,state.ConfigRevision))).SingleAsync(ct)).Value;
        var head=AggregateMongoTransaction.Read<AggregateConfigHead>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Configs).Find(new BsonDocument("_id",state.ConfigId)).SingleAsync(ct)).Value;
        var impact=new AggregateConfigImpactRequestDto(head.HeadRevision,version.Recipe,[new(instanceId,state.Revision)],[]);
        var ctx=await Context();var signed=(await Ok($"configs/{state.ConfigId}/queue-preview",new AggregateConfigImpactCommandDto(ctx,impact))).GetProperty("token").GetString()!;
        var beforeRevision=await Report();await Ok($"configs/{state.ConfigId}/queue",new AggregateConfigRevisionCommandDto(run+"-async-common",ctx,impact,signed));
        var moved=await Instance();
        Check(moved.ConfigRevision==head.HeadRevision+1&&(await Report()).PayloadRevision==beforeRevision.PayloadRevision,"common revision atomically pins selected Draft without waiting for evaluation");
        await worker.Run(instanceId,moved.Generation,ct);
        Check((await Instance()).AppliedGeneration==moved.Generation,"common revision computes selected instance with new pin");
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"asj1-native-"+run+".json"),JsonSerializer.Serialize(new{run,targetId,instanceId,state="PASS",checks=local,scope="NORMAL_DEVELOPMENT_PROGRAM_LOGIN_HTTP_SUBMIT_WITHDRAW_PRIVATE_HOST"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
