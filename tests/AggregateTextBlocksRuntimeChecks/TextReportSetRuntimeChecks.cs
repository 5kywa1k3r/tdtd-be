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
using tdtd_be.Controllers;

internal static class TextReportSetRuntimeChecks
{
    internal static async Task Content(MongoDbContext db, RuntimeFixture f, string run,
        Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,Action<bool,string> check,CancellationToken ct)
    {
        // Only this run's sources: deliberately no assignment start date or data window.
        await db.WorkAssignments.UpdateManyAsync(a=>a.WorkId==f.WorkId&&a.ParentAssignmentId!=null,
            Builders<WorkAssignment>.Update.Set(a=>a.StartDate,(DateTime?)null),cancellationToken:ct);
        await db.WorkTemplateAssignees.UpdateManyAsync(b=>b.WorkId==f.WorkId&&b.Id!=f.Binding.Id,
            Builders<WorkTemplateAssignee>.Update.Set(b=>b.StartDate,(DateTime?)null),cancellationToken:ct);
        Task<JsonElement> Call(string path,object body)=>post(path,body,f.Actor,HttpStatusCode.OK);
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var context=(await Call("editor/bootstrap",new{reportId=f.Report.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",TimeRules=[new(){Id="w",Mode="REPORT_SET",SourceDateBasis="REPORT_METADATA",Match="REPORTS",ReportSet=new(1,"AND",[])}],Nodes=[
            new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("text","TEXT","SET","n","w")]},
            new(){Id="c",Kind="CALCULATION",Inputs=[new("in","TEXT","SET")],Outputs=[new("out","TABLE","SINGLE")],Expressions=[new("out",new(){Kind="CALL",Name="REPORT_TEXT_TABLE",Arguments=[new(){Kind="INPUT",Ref="in"}],Options=new(){Order="UNIT_THEN_PERIOD",UnitDisplay="FULL_NAME"}})]},
            new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("body","TABLE","SINGLE","body")],Outputs=[]}],Edges=[new("a",new("s","text"),new("c","in")),new("b",new("c","out"),new("t","body"))]};
        var config=(await Call("configs",new AggregateConfigCreateCommandDto(context,new(run+"-content-config",f.Binding.Id,Pin(f.TargetForm),recipe)))).GetProperty("id").GetString()!;
        var id=(await Call("instances",new AggregateInstanceCreateCommandDto(run+"-content-instance",context,config))).GetProperty("id").GetString()!;
        var change=new AggregateMappingChangeDto(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
        var signed=await Call($"instances/{id}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change));
        await Call($"instances/{id}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-content-queue",context,change,signed.GetProperty("token").GetString()!));
        JsonElement read;do{await Task.Delay(100,ct);read=await Call($"instances/{id}/read",new AggregateInstanceReadCommandDto(context));}while(read.GetProperty("refresh").GetProperty("state").GetString() is "PENDING" or "RUNNING");
        check(read.GetProperty("refresh").GetProperty("state").GetString()=="COMPLETED","report-set paged content job without source start/window completes");
        var fresh=(await Call("editor/bootstrap",new{reportId=f.Report.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var submission=await Call("reports/"+f.Report.Id+"/submission-preview",new AggregateInstanceReadCommandDto(fresh));
        var reference=submission.GetProperty("instances")[0].GetProperty("preview").GetProperty("results").EnumerateArray().Single(r=>r.GetProperty("portId").GetString()=="body").GetProperty("value");
        var request=new AggregateContentReadRequest(f.Report.Id,null,null,null,reference,ListReadId:submission.GetProperty("listReadId").GetString());
        var page=await Call("content/page",request);
        check(page.GetProperty("rows").GetArrayLength()==2,"report-set submission read scope does not require schedule coverage");
        check(page.GetProperty("rows").EnumerateArray().All(r=>r.GetProperty("sample").GetString()!.Length<=240&&r.GetProperty("hasMore").GetBoolean()),"report-set content page keeps bounded text samples");
        var part=await Call("content/part",request with{RowKey=page.GetProperty("rows")[0].GetProperty("rowKey").GetString()});
        check(part.GetProperty("text").GetString()!.Length>1000,"report-set content detail loads whole source block on demand");
        await post("content/page",request,f.Outsider,HttpStatusCode.NotFound);
        check(true,"report-set paged content rejects another actor");
        var source=await db.WorkAssignmentReports.Find(r=>r.Id==f.Source.Id&&r.WorkId==f.WorkId).SingleAsync(ct);
        try
        {
            var changed=await db.WorkAssignmentReports.UpdateOneAsync(r=>r.Id==source.Id&&r.LifecycleRevision==source.LifecycleRevision&&r.PayloadHash==source.PayloadHash,
                Builders<WorkAssignmentReport>.Update.Set(r=>r.Status,tdtd_be.Models.Enums.WorkAssignmentReportStatus.Submitted).Inc(r=>r.LifecycleRevision,1),cancellationToken:ct);
            check(changed.ModifiedCount==1,"own content fixture source lifecycle changed with compare-and-set");
            var rejected=await post("content/page",request,f.Actor,HttpStatusCode.Conflict);
            check(rejected.GetProperty("code").GetString()=="AGG_INPUT_STALE","issued content scope rejects source no longer Approved");
        }
        finally
        {
            var restored=await db.WorkAssignmentReports.UpdateOneAsync(r=>r.Id==source.Id&&r.LifecycleRevision==source.LifecycleRevision+1&&r.PayloadHash==source.PayloadHash,
                Builders<WorkAssignmentReport>.Update.Set(r=>r.Status,source.Status).Set(r=>r.LifecycleRevision,source.LifecycleRevision),cancellationToken:ct);
            if(restored.ModifiedCount!=1)throw new Exception("Own report-set fixture restoration CAS failed: "+source.Id);
        }
        await FixtureProvenanceChecks.Migration(db,"report-set paged content readback",ct);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"report-set-content-"+run+".json"),JsonSerializer.Serialize(new{f.WorkId,reportId=f.Report.Id,instanceId=id,state="PASS"}),ct);
    }
    internal static async Task Once(MongoDbContext db, string run,
        Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,Action<bool,string> check,CancellationToken ct)
    {
        var f=await RuntimeFixture.Seed(db,run+"-report-set-once",ct,publishForm:(form,token)=>TextBrowserFixture.Publish(db,form,token));
        Task<JsonElement> Call(string path,object body)=>post(path,body,f.Actor,HttpStatusCode.OK);
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var boot=await Call("editor/bootstrap",new{reportId=f.Report.Id});
        check(boot.GetProperty("capabilities").GetProperty("reportSetFilter").GetProperty("supported").GetBoolean(),"report-set capability published by real BE");
        var context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",TimeRules=[new(){Id="w",Mode="REPORT_SET",SourceDateBasis="REPORT_METADATA",Match="REPORTS",ReportSet=new(1,"AND",[])}],Nodes=[
            new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("n","NUMBER","SET","n","w")]},
            new(){Id="c",Kind="CALCULATION",Inputs=[new("in","NUMBER","SET")],Outputs=[new("out","NUMBER","SINGLE")],Expressions=[new("out",new(){Kind="CALL",Name="SUM",Arguments=[new(){Kind="INPUT",Ref="in"}]})]},
            new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("total","NUMBER","SINGLE","total")],Outputs=[]}],Edges=[new("a",new("s","n"),new("c","in")),new("b",new("c","out"),new("t","total"))]};
        var config=(await Call("configs",new AggregateConfigCreateCommandDto(context,new(run+"-once-config",f.Binding.Id,Pin(f.TargetForm),recipe)))).GetProperty("id").GetString()!;
        var id=(await Call("instances",new AggregateInstanceCreateCommandDto(run+"-once-instance",context,config))).GetProperty("id").GetString()!;
        var stored=AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",id)).SingleAsync(ct)).Value;
        var change=new AggregateMappingChangeDto(stored.Revision,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
        var signed=await Call($"instances/{id}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change));
        check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==f.Report.PayloadRevision,"once confirmation does not write");
        await Call($"instances/{id}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-once-queue",context,change,signed.GetProperty("token").GetString()!));
        JsonElement read;do{await Task.Delay(100,ct);read=await Call($"instances/{id}/read",new AggregateInstanceReadCommandDto(context));}while(read.GetProperty("refresh").GetProperty("state").GetString() is "PENDING" or "RUNNING");
        check(read.GetProperty("refresh").GetProperty("state").GetString()=="COMPLETED","once job completes without source or target window declarations");
        var target=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);
        var fields=JsonSerializer.Deserialize<JsonElement>((await new WorkReportPayloadService(db).LoadReportPayloadAsync(target,ct)).FieldValuesJson!);
        check(fields.GetProperty("total").GetDecimal()==30&&target.PayloadRevision==f.Report.PayloadRevision+1,"once authoritative payload is 30 with one revision");
        check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Source.Id).SingleAsync(ct)).PayloadHash==f.Source.PayloadHash,"once source unchanged");
        await FixtureProvenanceChecks.Migration(db,"report-set once after readback",ct);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"report-set-once-"+run+".json"),JsonSerializer.Serialize(new{f.WorkId,reportId=f.Report.Id,sourceId=f.Source.Id,instanceId=id,state="PASS",target.PayloadRevision}),ct);
    }
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
            if(isTarget) await store.ExecuteAsync(async(tx,token)=>{foreach(var key in new[]{("REPORT",r.Id),("SLOT",r.PeriodInstanceKey)})await tx.PutAsync(AggregateCollections.Declarations,key.Item1+":"+key.Item2,0,
                new AggregateDeclarationState(key.Item1,key.Item2,f.WorkId,r.WorkAssignmentId,new(start,end,"USER_DECLARED",run,1)),f.WorkId,key.Item2,[],token);return true;},ct);
            (isTarget?targets:sources).Add(r);
        }
        await Seed(true,0,"2026-09-30","2026-09-01","2026-09-30",0);await Seed(true,1,"2026-10-30","2026-10-01","2026-10-31",0);
        await Seed(false,0,"2026-09-21","2026-09-14","2026-09-20",10);await Seed(false,1,"2026-09-28","2026-09-21","2026-09-27",20);
        await Seed(false,2,"2026-10-05","2026-09-28","2026-10-04",30);
        await FixtureProvenanceChecks.Migration(db,"P05 report-set final fixture pins",ct);
        Task<JsonElement> Call(string route,object body)=>post(route,body,f.Actor,HttpStatusCode.OK);
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",TimeRules=[new(){Id="w",Mode="REPORT_SET",SourceDateBasis="REPORT_METADATA",Match="REPORTS",ReportSet=new(1,"AND",[])}],Nodes=[
            new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("n","NUMBER","SET","n","w")]},
            new(){Id="c",Kind="CALCULATION",Inputs=[new("in","NUMBER","SET")],Outputs=[new("out","NUMBER","SINGLE")],Expressions=[new("out",new(){Kind="CALL",Name="SUM",Arguments=[new(){Kind="INPUT",Ref="in"}]})]},
            new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("total","NUMBER","SINGLE","total")],Outputs=[]}],Edges=[new("a",new("s","n"),new("c","in")),new("b",new("c","out"),new("t","total"))]};
        var contexts=new List<AggregatePeriodContextDto>();var instances=new List<string>();var evidence=new List<object>();var local=0;
        void Check(bool condition,string label){check(condition,"P05 report-set "+label);local++;}
        foreach(var target in targets)contexts.Add((await Call("editor/bootstrap",new{reportId=target.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!);
        var configId=(await Call("configs",new AggregateConfigCreateCommandDto(contexts[0],new(run+"-mixed",f.Binding.Id,Pin(f.TargetForm),recipe)))).GetProperty("id").GetString()!;
        for(var i=0;i<2;i++)instances.Add((await Call("instances",new AggregateInstanceCreateCommandDto(run+"-mixed-"+i,contexts[i],configId))).GetProperty("id").GetString()!);
        foreach(var scenario in new[]{(0,"ALL",(decimal?)60,3),(0,"AFTER",(decimal?)50,2),(1,"BEFORE",(decimal?)10,1),(1,"EMPTY_COMPLETION",(decimal?)60,3)}){
            var (index,match,expected,count)=scenario;var id=instances[index];
            var state=AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",id)).SingleAsync(ct)).Value;
            var before=await db.WorkAssignmentReports.Find(r=>r.Id==targets[index].Id).SingleAsync(ct);var other=await db.WorkAssignmentReports.Find(r=>r.Id==targets[1-index].Id).SingleAsync(ct);
            var overlay=new AggregateInstanceOverrideDto(AggregateCanonical.Hash(recipe),[recipe.TimeRules[0] with{ReportSet=new(1,"AND",match switch {"AFTER"=>[new("DUE","GE",["2026-09-28"])],"BEFORE"=>[new("DUE","LE",["2026-09-21"])],"EMPTY_COMPLETION"=>[new("COMPLETED","ABSENT",[])],_=>[]})}],[],[]);
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
            Check(state.Applied!.Preview.Issues.Any(issue=>issue.Code=="AGG_OVERLAP_WHOLE_REPORT")== (match=="OVERLAPS_WHOLE_REPORT"),"metadata filters do not invent whole-window overlap");
            var unchanged=await db.WorkAssignmentReports.Find(r=>r.Id==other.Id).SingleAsync(ct);
            Check(current.PayloadRevision==before.PayloadRevision+1&&unchanged.PayloadHash==other.PayloadHash&&unchanged.PayloadRevision==other.PayloadRevision,"one period write preserves other period");
            evidence.Add(new{month=index+9,match,expected,actual,contributors=state.Applied!.Preview.ContributingSources.Select(p=>p.ReportId),current.PayloadRevision});
        }
        var sourceIds=sources.Select(s=>s.Id).ToArray();var sourceAfter=await db.WorkAssignmentReports.Find(r=>sourceIds.Contains(r.Id)).ToListAsync(ct);
        Check(sourceAfter.Count==3&&sourceAfter.All(r=>sources.Single(s=>s.Id==r.Id).PayloadHash==r.PayloadHash),"source identities and all source payload hashes preserved; no source data windows seeded");
        await FixtureProvenanceChecks.Migration(db,"P05 report-set after readback",ct);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"p05-report-set-"+run+".json"),JsonSerializer.Serialize(new{run,state="PASS",checks=local,f.WorkId,configId,instanceIds=instances,targetIds=targets.Select(t=>t.Id),sourceIds=sources.Select(s=>s.Id),evidence,
            scope="REAL_API_QUEUED_JOB_AUTHORITATIVE_READBACK_APPROVED_SOURCE_FIXTURE_NOT_BROWSER"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
