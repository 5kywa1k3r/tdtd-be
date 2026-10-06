using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextMixedCalendarChecks
{
    internal static async Task Run(MongoDbContext db,AggregateMongoStore store,string run,
        Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,Action<bool,string> check,CancellationToken ct)
    {
        var f=await RuntimeFixture.Seed(db,run+"-mixed",ct,publishForm:(form,token)=>TextBrowserFixture.Publish(db,form,token));
        var payload=new WorkReportPayloadService(db);var bindings=await db.WorkTemplateAssignees.Find(b=>b.WorkId==f.WorkId).ToListAsync(ct);
        var monthly=new AssignmentSchedule{CycleType="MONTHLY",StartDate=new DateTime(2026,9,1,0,0,0,DateTimeKind.Utc),MonthDays=[30]};
        var weekly=new AssignmentSchedule{CycleType="WEEKLY",StartDate=new DateTime(2026,9,21,0,0,0,DateTimeKind.Utc),WeekDays=[1]};
        foreach(var b in bindings){var parent=b.Id==f.Binding.Id;b.AssignmentType="PERIODIC_REPORT";b.Schedule=parent?monthly:weekly;b.StartDate=b.Schedule.StartDate;
            b.DueDate=new DateTime(2026,10,parent?30:5,0,0,0,DateTimeKind.Utc);
            await db.WorkTemplateAssignees.ReplaceOneAsync(x=>x.Id==b.Id,b,cancellationToken:ct);
            await db.WorkAssignments.UpdateOneAsync(a=>a.Id==b.WorkAssignmentId,Builders<WorkAssignment>.Update.Set(a=>a.AssignmentType,"PERIODIC_REPORT").Set(a=>a.Schedule,b.Schedule),cancellationToken:ct);}
        var targets=new List<WorkAssignmentReport>();var sources=new List<WorkAssignmentReport>();
        async Task Seed(bool isTarget,int index,string due,string start,string end,int number){
            var seed=isTarget?f.Report:f.Source;var r=BsonSerializer.Deserialize<WorkAssignmentReport>(seed.ToBson());var binding=bindings.Single(b=>b.WorkAssignmentId==r.WorkAssignmentId);
            if(index>0){r.Id=ObjectId.GenerateNewId().ToString();r.WorkReportPeriodId=ObjectId.GenerateNewId().ToString();r.PayloadRevision=0;r.PayloadHash=null;}
            r.DueAtUtc=DateTime.SpecifyKind(DateTime.ParseExact(due,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),DateTimeKind.Utc);
            r.PeriodKey=r.DueAtUtc.Value.ToString("yyyyMMdd");r.PeriodInstanceKey=binding.Id+":"+r.PeriodKey;
            var saved=await payload.SaveReportPayloadAsync(r,"[]",isTarget?"{}":JsonSerializer.Serialize(new{n=number}),null,null,f.Actor,DateTime.UtcNow,ct);
            r.PayloadRevision=saved.PayloadRevision;r.PayloadHash=saved.PayloadHash;r.PayloadSizeBytes=saved.PayloadSizeBytes;r.PayloadStatus=saved.PayloadStatus;
            await db.WorkAssignmentReports.ReplaceOneAsync(x=>x.Id==r.Id,r,new ReplaceOptions{IsUpsert=true},ct);
            await db.WorkReportPeriods.ReplaceOneAsync(p=>p.Id==r.WorkReportPeriodId,new WorkReportPeriod{Id=r.WorkReportPeriodId!,WorkId=f.WorkId,WorkAssignmentId=r.WorkAssignmentId,
                WorkTemplateAssigneeId=binding.Id,AssigneeUserId=r.AssigneeUserId,AssigneeUnitId=binding.AssigneeUnitId,CurrentReportId=r.Id,PeriodKey=r.PeriodKey,PeriodInstanceKey=r.PeriodInstanceKey,DueAtUtc=r.DueAtUtc,
                DynamicFormTemplateId=r.DynamicFormTemplateId,DynamicFormFamilyId=r.DynamicFormFamilyId,DynamicFormVersionNo=r.DynamicFormVersionNo,DynamicFormSchemaHash=r.DynamicFormSchemaHash,CreatedByUserId=f.Actor},new ReplaceOptions{IsUpsert=true},ct);
            await store.ExecuteAsync(async(tx,token)=>{foreach(var key in new[]{("REPORT",r.Id),("SLOT",r.PeriodInstanceKey)})await tx.PutAsync(AggregateCollections.Declarations,key.Item1+":"+key.Item2,0,
                new AggregateDeclarationState(key.Item1,key.Item2,f.WorkId,r.WorkAssignmentId,new(start,end,"USER_DECLARED",run,1)),f.WorkId,key.Item2,[],token);return true;},ct);
            (isTarget?targets:sources).Add(r);
        }
        await Seed(true,0,"2026-09-30","2026-09-01","2026-09-30",0);await Seed(true,1,"2026-10-30","2026-10-01","2026-10-31",0);
        await Seed(false,0,"2026-09-21","2026-09-14","2026-09-20",10);await Seed(false,1,"2026-09-28","2026-09-21","2026-09-27",20);
        await Seed(false,2,"2026-10-05","2026-09-28","2026-10-04",30);
        await FixtureProvenanceChecks.Migration(db,"P05 weekly monthly final fixture pins",ct);
        Task<JsonElement> Call(string route,object body)=>post(route,body,f.Actor,HttpStatusCode.OK);
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",TimeRules=[new(){Id="w",Mode="TARGET_DATA_WINDOW",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED"}],Nodes=[
            new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("n","NUMBER","SET","n","w")]},
            new(){Id="c",Kind="CALCULATION",Inputs=[new("in","NUMBER","SET")],Outputs=[new("out","NUMBER","SINGLE")],Expressions=[new("out",new(){Kind="CALL",Name="SUM",Arguments=[new(){Kind="INPUT",Ref="in"}]})]},
            new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("total","NUMBER","SINGLE","total")],Outputs=[]}],Edges=[new("a",new("s","n"),new("c","in")),new("b",new("c","out"),new("t","total"))]};
        var contexts=new List<AggregatePeriodContextDto>();var instances=new List<string>();var evidence=new List<object>();var local=0;
        void Check(bool condition,string label){check(condition,"P05 weekly-monthly "+label);local++;}
        foreach(var target in targets)contexts.Add((await Call("editor/bootstrap",new{reportId=target.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!);
        var configId=(await Call("configs",new AggregateConfigCreateCommandDto(contexts[0],new(run+"-mixed",f.Binding.Id,Pin(f.TargetForm),recipe)))).GetProperty("id").GetString()!;
        for(var i=0;i<2;i++)instances.Add((await Call("instances",new AggregateInstanceCreateCommandDto(run+"-mixed-"+i,contexts[i],configId))).GetProperty("id").GetString()!);
        foreach(var scenario in new[]{(0,"CONTAINED",(decimal?)30,2),(0,"OVERLAPS_WHOLE_REPORT",(decimal?)60,3),(1,"CONTAINED",(decimal?)null,0),(1,"OVERLAPS_WHOLE_REPORT",(decimal?)30,1)}){
            var (index,match,expected,count)=scenario;var id=instances[index];
            var state=AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",id)).SingleAsync(ct)).Value;
            var before=await db.WorkAssignmentReports.Find(r=>r.Id==targets[index].Id).SingleAsync(ct);var other=await db.WorkAssignmentReports.Find(r=>r.Id==targets[1-index].Id).SingleAsync(ct);
            var overlay=new AggregateInstanceOverrideDto(AggregateCanonical.Hash(recipe),[recipe.TimeRules[0] with{Match=match}],[],[]);
            var change=new AggregateMappingChangeDto(state.Revision,overlay,new([new("s","FORM_SELECTOR",[],[])]),[],false);
            var signed=await Call($"instances/{id}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(contexts[index],change));
            Check((await db.WorkAssignmentReports.Find(r=>r.Id==before.Id).SingleAsync(ct)).PayloadRevision==before.PayloadRevision,"confirmation preview does not write "+index+"/"+match);
            await Call($"instances/{id}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-mixed-"+index+match,contexts[index],change,signed.GetProperty("token").GetString()!));
            JsonElement read;do{await Task.Delay(100,ct);read=await Call($"instances/{id}/read",new AggregateInstanceReadCommandDto(contexts[index]));}while(read.GetProperty("refresh").GetProperty("state").GetString() is "PENDING" or "RUNNING");
            Check(read.GetProperty("refresh").GetProperty("state").GetString()=="COMPLETED","background completed "+index+"/"+match+": "+read.GetProperty("refresh").GetProperty("errorCode"));
            state=AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",id)).SingleAsync(ct)).Value;
            var current=await db.WorkAssignmentReports.Find(r=>r.Id==before.Id).SingleAsync(ct);var fields=JsonSerializer.Deserialize<JsonElement>((await payload.LoadReportPayloadAsync(current,ct)).FieldValuesJson!);
            var actual=fields.TryGetProperty("total",out var number)&&number.ValueKind==JsonValueKind.Number?number.GetDecimal():(decimal?)null;
            Check(actual==expected&&state.Applied!.Preview.ContributingSources.Count==count,"exact whole reports, no proration/fake zero "+index+"/"+match);
            Check(state.Applied!.Preview.Issues.Any(issue=>issue.Code=="AGG_OVERLAP_WHOLE_REPORT")== (match=="OVERLAPS_WHOLE_REPORT"),"whole-report overlap warning follows explicit selection");
            var unchanged=await db.WorkAssignmentReports.Find(r=>r.Id==other.Id).SingleAsync(ct);
            Check(current.PayloadRevision==before.PayloadRevision+1&&unchanged.PayloadHash==other.PayloadHash&&unchanged.PayloadRevision==other.PayloadRevision,"one period write preserves other period");
            evidence.Add(new{month=index+9,match,expected,actual,contributors=state.Applied!.Preview.ContributingSources.Select(p=>p.ReportId),current.PayloadRevision});
        }
        var sourceIds=sources.Select(s=>s.Id).ToArray();var sourceAfter=await db.WorkAssignmentReports.Find(r=>sourceIds.Contains(r.Id)).ToListAsync(ct);
        Check(sourceAfter.Count==3&&sourceAfter.All(r=>sources.Single(s=>s.Id==r.Id).PayloadHash==r.PayloadHash),"source identities and all source payload hashes preserved");
        await FixtureProvenanceChecks.Migration(db,"P05 weekly monthly after readback",ct);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"p05-weekly-monthly-"+run+".json"),JsonSerializer.Serialize(new{run,state="PASS",checks=local,f.WorkId,configId,instanceIds=instances,targetIds=targets.Select(t=>t.Id),sourceIds=sources.Select(s=>s.Id),evidence,
            scope="REAL_API_QUEUED_JOB_AUTHORITATIVE_READBACK_APPROVED_SOURCE_FIXTURE_NOT_BROWSER"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
