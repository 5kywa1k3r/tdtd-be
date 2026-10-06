using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextPeriodicChecks
{
    internal static async Task Run(MongoDbContext db,AggregateMongoStore store,string run,AggregateRecipeDto prototype,
        Action<DynamicFormTemplate,bool> configure,Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,
        Action<bool,string> check,CancellationToken ct,bool withLifecycle=false)
    {
        void ConfigurePeriodic(DynamicFormTemplate form,bool target){
            configure(form,target);
            var fields=JsonNode.Parse(form.FieldsJson)!.AsArray();
            fields.Add(JsonSerializer.SerializeToNode(new{id=target?"metric":"score",key=target?"metric":"score",name=target?"Kết quả số":"Số phát sinh trong kỳ",sectionId="main",type="number",required=false}));
            form.FieldsJson=fields.ToJsonString();
        }
        var f=await RuntimeFixture.Seed(db,run+"-monthly",ct,configureForm:ConfigurePeriodic,publishForm:(form,token)=>TextBrowserFixture.Publish(db,form,token));
        var bindings=await db.WorkTemplateAssignees.Find(b=>b.WorkId==f.WorkId).ToListAsync(ct);
        var schedule=new AssignmentSchedule{CycleType="MONTHLY",StartDate=new DateTime(2026,9,1,0,0,0,DateTimeKind.Utc),MonthDays=[30]};
        foreach(var binding in bindings){binding.AssignmentType="PERIODIC_REPORT";binding.Schedule=schedule;binding.DueDate=new DateTime(2026,11,30,0,0,0,DateTimeKind.Utc);
            await db.WorkTemplateAssignees.ReplaceOneAsync(b=>b.Id==binding.Id,binding,cancellationToken:ct);}
        await db.WorkAssignments.UpdateManyAsync(a=>a.WorkId==f.WorkId,Builders<WorkAssignment>.Update.Set(a=>a.AssignmentType,"PERIODIC_REPORT").Set(a=>a.Schedule,schedule),cancellationToken:ct);
        var payload=new WorkReportPayloadService(db);
        var targets=new List<WorkAssignmentReport>();var sources=new List<WorkAssignmentReport>();
        var blocks=new Dictionary<string,string>();
        foreach(var month in new[]{9,10,11}){
            var date=new DateTime(2026,month,30,0,0,0,DateTimeKind.Utc);
            var declaration=new AggregateDataWindowDeclarationDto($"2026-{month:00}-01",$"2026-{month:00}-{DateTime.DaysInMonth(2026,month):00}","USER_DECLARED",run,1);
            foreach(var seed in new[]{f.Report,f.Source}){
                var report=BsonSerializer.Deserialize<WorkAssignmentReport>(seed.ToBson());
                if(month!=9){report.Id=ObjectId.GenerateNewId().ToString();report.WorkReportPeriodId=ObjectId.GenerateNewId().ToString();report.PayloadRevision=0;}
                var binding=bindings.Single(b=>b.WorkAssignmentId==report.WorkAssignmentId);
                report.PeriodKey=$"2026{month:00}30";report.PeriodInstanceKey=binding.Id+":"+report.PeriodKey;report.DueAtUtc=date;
                var original=await payload.LoadReportPayloadAsync(seed,ct);
                var block=$"Báo cáo tháng {month}\n"+new string(month==9?'a':'b',1001);blocks[report.Id]=block;
                var fields=seed.Id==f.Report.Id?"{}":JsonSerializer.Serialize(new{n=new[]{$"Báo cáo tháng {month}",new string(month==9?'a':'b',1001)},score=(month-8)*10});
                var saved=await payload.SaveReportPayloadAsync(report,"[]",fields,original.TableValuesJson,null,f.Actor,DateTime.UtcNow,ct);
                report.PayloadRevision=saved.PayloadRevision;report.PayloadHash=saved.PayloadHash;report.PayloadSizeBytes=saved.PayloadSizeBytes;report.PayloadStatus=saved.PayloadStatus;
                await db.WorkAssignmentReports.ReplaceOneAsync(r=>r.Id==report.Id,report,new ReplaceOptions{IsUpsert=true},ct);
                var period=new WorkReportPeriod{Id=report.WorkReportPeriodId!,WorkId=f.WorkId,WorkAssignmentId=binding.WorkAssignmentId,WorkTemplateAssigneeId=binding.Id,CurrentReportId=report.Id,
                    AssigneeUserId=binding.AssigneeUserId,AssigneeUnitId=binding.AssigneeUnitId,PeriodKey=report.PeriodKey,PeriodInstanceKey=report.PeriodInstanceKey,DueAtUtc=date,
                    DynamicFormTemplateId=report.DynamicFormTemplateId,DynamicFormFamilyId=report.DynamicFormFamilyId,DynamicFormVersionNo=report.DynamicFormVersionNo,DynamicFormSchemaHash=report.DynamicFormSchemaHash,CreatedByUserId=f.Actor};
                await db.WorkReportPeriods.ReplaceOneAsync(p=>p.Id==period.Id,period,new ReplaceOptions{IsUpsert=true},ct);
                await store.ExecuteAsync(async(tx,cancel)=>{
                    foreach(var key in new[]{("REPORT",report.Id),("SLOT",report.PeriodInstanceKey)})await tx.PutAsync(AggregateCollections.Declarations,key.Item1+":"+key.Item2,0,
                        new AggregateDeclarationState(key.Item1,key.Item2,f.WorkId,report.WorkAssignmentId,declaration),f.WorkId,key.Item2,[],cancel);
                    return true;
                },ct);
                (seed.Id==f.Report.Id?targets:sources).Add(report);
            }
        }
        await FixtureProvenanceChecks.Migration(db,"R8 monthly final pins",ct);
        Task<JsonElement> Call(string path,object body)=>post(path,body,f.Actor,HttpStatusCode.OK);
        async Task<AggregatePeriodContextDto> Context(WorkAssignmentReport report)=>(await Call("editor/bootstrap",new{reportId=report.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var recipe=prototype with{Nodes=prototype.Nodes.Select(n=>n.Kind=="SOURCE"?n with{Form=Pin(f.SourceForm)}:n.Kind=="TARGET"?n with{Form=Pin(f.TargetForm)}:n).ToList()};
        var first=await Context(targets[0]);var second=await Context(targets[1]);
        check(first.Kind=="PERIODIC"&&second.PeriodKey=="20261030"&&second.DataEndDate=="2026-10-31","R8 monthly data window stays separate from due-day identity");
        var config=await Call("configs",new AggregateConfigCreateCommandDto(first,new(run+"-monthly-config",first.BindingId,Pin(f.TargetForm),recipe)));
        var configId=config.GetProperty("id").GetString()!;var ids=new List<string>();
        for(var i=0;i<2;i++){
            var context=await Context(targets[i]);var instance=await Call("instances",new AggregateInstanceCreateCommandDto(run+"-monthly-instance-"+i,context,configId));
            var id=instance.GetProperty("id").GetString()!;ids.Add(id);
            var change=new AggregateMappingChangeDto(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
            var job=await Call("preview-jobs/start",new AggregatePreviewJobRequest("MAPPING",context,null,id,change));
            while(job.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){
                await Task.Delay(150,ct);job=await Call("preview-jobs/"+job.GetProperty("id").GetString()+"/read",new{});
            }
            check(job.GetProperty("state").GetString()=="COMPLETED","R8 monthly Hangfire preview completed "+targets[i].PeriodKey);
            var prepared=job.GetProperty("result");var preview=prepared.GetProperty("preview").GetProperty("preview");
            check(preview.GetProperty("linkedSources").GetArrayLength()==1&&preview.GetProperty("linkedSources")[0].GetProperty("reportId").GetString()==sources[i].Id,"R8 monthly selection contains only matching month's report "+i);
            await Call("instances/"+id+"/apply",new AggregateMappingApplyCommandDto(run+"-monthly-apply-"+i,context,change,prepared.GetProperty("token").GetString()!));
            var report=await db.WorkAssignmentReports.Find(r=>r.Id==targets[i].Id).SingleAsync(ct);
            var readback=JsonSerializer.Deserialize<JsonElement>((await payload.LoadReportPayloadAsync(report,ct)).FieldValuesJson!);
            check(readback.GetProperty("total").GetArrayLength()==1&&readback.GetProperty("total")[0].GetString()==blocks[sources[i].Id]&&readback.GetProperty("plain").GetString()==blocks[sources[i].Id],"R8 monthly native string[] and plain Apply/readback "+i);
        }
        check(ids[0]!=ids[1],"R8 shared config has separate per-period instances");
        await TextPeriodicRevisionChecks.Run(db,store,payload,f,run,recipe,configId,ids,targets,sources,blocks,post,check,ct);
        if(withLifecycle)await TextPeriodicLifecycleChecks.Run(db,store,payload,f,run,configId,ids,targets,sources,check,ct);
        await FixtureProvenanceChecks.Migration(db,"R8 monthly after Apply",ct);
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/text-monthly-"+run+".json",JsonSerializer.Serialize(new{run,workId=f.WorkId,targetReportIds=targets.Select(r=>r.Id),sourceReportIds=sources.Select(r=>r.Id),configId,instanceIds=ids,state="PASS"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
