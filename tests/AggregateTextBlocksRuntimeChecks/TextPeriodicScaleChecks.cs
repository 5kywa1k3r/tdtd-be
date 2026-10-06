using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Isolated load fixture: final schemas before pins; no production cap/config changes.
internal static class TextPeriodicScaleChecks
{
    internal static async Task Inspect(MongoDbContext db,string workId,CancellationToken ct)
    {
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(work.AutoCode,"^r8-text-20261005-[0-9a-f]{8}-scale$"))throw new InvalidOperationException("Only this harness's own scale fixture may be inspected.");
        var reports=await db.WorkAssignmentReports.Find(r=>r.WorkId==workId&&!r.IsDeleted).ToListAsync(ct);
        var intents=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Refresh).Find(new BsonDocument("workId",workId)).ToListAsync(ct);
        var instances=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("workId",workId)).ToListAsync(ct);
        var evidence=new{mode="READ_ONLY_OWN_SCALE_INSPECTION",at=DateTimeOffset.UtcNow,workId,work.AutoCode,
            sourceCount=reports.Count(r=>r.AssigneeUserId!=work.CreatedByUserId),
            targets=reports.Where(r=>r.AssigneeUserId==work.CreatedByUserId).Select(r=>new{r.Id,r.PeriodKey,r.Status,r.PayloadRevision,r.PayloadHash}),
            intents=intents.Select(row=>AggregateMongoTransaction.Read<AggregateRefreshIntent>(row).Value),
            instances=instances.Select(row=>{var v=AggregateMongoTransaction.Read<AggregateInstanceState>(row).Value;return new{v.Id,v.Revision,v.Generation,v.AppliedGeneration,v.State,hasApplied=v.Applied!=null};})};
        var json=JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-scale-inspection-"+workId+".json",json,ct);
        await FixtureProvenanceChecks.Migration(db,"P05 scale read-only inspection",ct);
    }
    internal static async Task Run(MongoDbContext db,AggregateMongoStore store,IDynamicFlowDefinitionTransactionRunner runner,
        string run,Action<DynamicFormTemplate,bool> configure,
        Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,Action<bool,string> check,CancellationToken ct,bool approvalArrival=false)
    {
        var returnFocus=approvalArrival&&Environment.GetEnvironmentVariable("AGG_REPORT_SET_RETURN")=="1";
        var units=returnFocus?3:166;
        var watch=Stopwatch.StartNew();
        void Configure(DynamicFormTemplate form,bool target){
            configure(form,target);
            var fields=JsonNode.Parse(form.FieldsJson)!.AsArray();
            fields.Add(JsonSerializer.SerializeToNode(new{id=target?"metric":"score",key=target?"metric":"score",name="Giá trị thử tải",sectionId="main",type="number"}));
            form.FieldsJson=fields.ToJsonString();
        }
        var f=await RuntimeFixture.Seed(db,run+"-scale",ct,configureForm:Configure,publishForm:(form,token)=>TextBrowserFixture.Publish(db,form,token));
        string Id()=>ObjectId.GenerateNewId().ToString();
        T Clone<T>(T value)=>BsonSerializer.Deserialize<T>(value!.ToBson());
        var payload=new WorkReportPayloadService(db);
        var templateAssignment=await db.WorkAssignments.Find(a=>a.Id==f.Source.WorkAssignmentId).SingleAsync(ct);
        var templateBinding=await db.WorkTemplateAssignees.Find(b=>b.WorkAssignmentId==f.Source.WorkAssignmentId).SingleAsync(ct);
        var schedule=new AssignmentSchedule{CycleType="MONTHLY",StartDate=new DateTime(2026,approvalArrival?9:1,1,0,0,0,DateTimeKind.Utc),MonthDays=[28]};
        var due=new DateTime(2026,approvalArrival?9:12,28,0,0,0,DateTimeKind.Utc);
        var targets=new List<WorkAssignmentReport>();
        var sourceIds=new List<string>();
        var sourceHashes=new Dictionary<string,string?>();
        var emptyTables=(await payload.LoadReportPayloadAsync(f.Report,ct)).TableValuesJson;
        void Periodic(WorkTemplateAssignee b){b.AssignmentType="PERIODIC_REPORT";b.Schedule=schedule;b.StartDate=schedule.StartDate;b.DueDate=due;}
        async Task Declare(WorkAssignmentReport r,int month){
            var window=new AggregateDataWindowDeclarationDto($"2026-{month:00}-01",$"2026-{month:00}-{DateTime.DaysInMonth(2026,month):00}","USER_DECLARED",run,1);
            await store.ExecuteAsync(async(tx,cancel)=>{
                foreach(var key in new[]{("REPORT",r.Id),("SLOT",r.PeriodInstanceKey!)})
                    await tx.PutAsync(AggregateCollections.Declarations,key.Item1+":"+key.Item2,0,new AggregateDeclarationState(key.Item1,key.Item2,f.WorkId,r.WorkAssignmentId,window),f.WorkId,key.Item2,[],cancel);
                return true;
            },ct);
        }
        async Task<WorkAssignmentReport> SeedReport(WorkTemplateAssignee b,int month,int ordinal,bool reuse){
            var target=ordinal==0;var r=Clone(target?f.Report:f.Source);
            if(!reuse){r.Id=Id();r.WorkReportPeriodId=Id();r.PayloadRevision=0;r.PayloadHash=null;}
            r.WorkAssignmentId=b.WorkAssignmentId;r.AssigneeUserId=b.AssigneeUserId;r.CreatedByUserId=b.AssigneeUserId;
            r.PeriodKey=$"2026{month:00}28";r.PeriodInstanceKey=b.Id+":"+r.PeriodKey;
            r.DueAtUtc=new DateTime(2026,month,28,0,0,0,DateTimeKind.Utc);r.ReportTitle=$"[THỬ TẢI RIÊNG] Đơn vị {ordinal} tháng {month}";
            var fields=target?"{}":JsonSerializer.Serialize(new{score=ordinal*(approvalArrival?1:month),n=new[]{$"Đơn vị {ordinal}, tháng {month}",new string('x',1001)}});
            var saved=await payload.SaveReportPayloadAsync(r,"[]",fields,target?emptyTables:null,null,f.Actor,DateTime.UtcNow,ct);
            r.PayloadRevision=saved.PayloadRevision;r.PayloadHash=saved.PayloadHash;r.PayloadSizeBytes=saved.PayloadSizeBytes;r.PayloadStatus=saved.PayloadStatus;
            if(approvalArrival&&!target){r.Status=tdtd_be.Models.Enums.WorkAssignmentReportStatus.Submitted;r.SubmittedAtUtc=DateTime.UtcNow;}
            await db.WorkAssignmentReports.ReplaceOneAsync(x=>x.Id==r.Id,r,new ReplaceOptions{IsUpsert=true},ct);
            var p=new WorkReportPeriod{Id=r.WorkReportPeriodId!,WorkId=f.WorkId,WorkAssignmentId=b.WorkAssignmentId,WorkTemplateAssigneeId=b.Id,
                AssigneeUserId=b.AssigneeUserId,AssigneeUnitId=b.AssigneeUnitId,CurrentReportId=r.Id,PeriodKey=r.PeriodKey,PeriodInstanceKey=r.PeriodInstanceKey,DueAtUtc=r.DueAtUtc,
                DynamicFormTemplateId=r.DynamicFormTemplateId,DynamicFormFamilyId=r.DynamicFormFamilyId,DynamicFormVersionNo=r.DynamicFormVersionNo,DynamicFormSchemaHash=r.DynamicFormSchemaHash,CreatedByUserId=f.Actor};
            await db.WorkReportPeriods.ReplaceOneAsync(x=>x.Id==p.Id,p,new ReplaceOptions{IsUpsert=true},ct);
            await Declare(r,month);return r;
        }
        Periodic(f.Binding);
        await db.WorkTemplateAssignees.ReplaceOneAsync(b=>b.Id==f.Binding.Id,f.Binding,cancellationToken:ct);
        await db.WorkAssignments.UpdateOneAsync(a=>a.Id==f.Binding.WorkAssignmentId,Builders<WorkAssignment>.Update.Set(a=>a.AssignmentType,"PERIODIC_REPORT").Set(a=>a.Schedule,schedule),cancellationToken:ct);
        foreach(var month in approvalArrival?new[]{9}:new[]{10,11,12})targets.Add(await SeedReport(f.Binding,month,0,approvalArrival||month==10));
        for(var unit=1;unit<=units;unit++){
            var assignment=Clone(templateAssignment);var binding=Clone(templateBinding);
            if(unit>1){
                var user=Id();var unitId=Id();assignment.Id=Id();assignment.Code=run+"-scale-"+unit;assignment.Path="/"+f.Binding.WorkAssignmentId+"/"+assignment.Id;
                assignment.Assignees=[new(){UserId=user,UnitId=unitId}];assignment.TargetUnitIds=[unitId];
                binding.Id=Id();binding.WorkAssignmentId=assignment.Id;binding.AssigneeUserId=user;binding.AssigneeUnitId=unitId;
                await db.Users.InsertOneAsync(new AppUser{Id=user,Username=run+"-scale-"+unit,UnitId=unitId,CreatedByUserId=f.Actor},cancellationToken:ct);
            }
            assignment.AssignmentType="PERIODIC_REPORT";assignment.Schedule=schedule;Periodic(binding);
            await db.Units.InsertOneAsync(new Unit{Id=binding.AssigneeUnitId!,Code=run+"-scale-"+unit,FullName="Đơn vị thử tải "+unit,ShortName="ĐV "+unit,Note=run,CreatedByUserId=f.Actor},cancellationToken:ct);
            await db.WorkAssignments.ReplaceOneAsync(a=>a.Id==assignment.Id,assignment,new ReplaceOptions{IsUpsert=true},ct);
            await db.WorkTemplateAssignees.ReplaceOneAsync(b=>b.Id==binding.Id,binding,new ReplaceOptions{IsUpsert=true},ct);
            foreach(var month in approvalArrival?new[]{9}:Enumerable.Range(1,12)){
                var r=await SeedReport(binding,month,unit,unit==1&&(approvalArrival||month==1));sourceIds.Add(r.Id);sourceHashes[r.Id]=r.PayloadHash;
            }
            if(unit%20==0)Console.WriteLine($"SCALE_SEED units={unit} reports={sourceIds.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
        var seedMs=watch.ElapsedMilliseconds;
        Console.WriteLine($"SCALE_SEEDED work={f.WorkId} reports={sourceIds.Count} seedMs={seedMs}");
        await FixtureProvenanceChecks.Migration(db,"P05 scale final schema/payload pins",ct);
        Task<JsonElement> Call(string path,object body)=>post(path,body,f.Actor,HttpStatusCode.OK);
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        AggregateExpressionDto Input(string name)=>new(){Kind="INPUT",Ref=name};
        var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",
            TimeRules=[new(){Id="w",Mode="CUMULATIVE_FROM",StartDate="2026-01-01",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED"}],Nodes=[
            new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("number","NUMBER","SET","score","w"),new("text","TEXT","SET","n","w")]},
            new(){Id="c",Kind="CALCULATION",Inputs=[new("number","NUMBER","SET"),new("text","TEXT","SET")],Outputs=[new("sum","NUMBER","SINGLE"),new("table","TABLE","SINGLE")],Expressions=[new("sum",new(){Kind="CALL",Name="SUM",Arguments=[Input("number")]}),new("table",new(){Kind="CALL",Name="REPORT_TEXT_TABLE",Arguments=[Input("text")],Options=new(){Order="UNIT_THEN_PERIOD",UnitDisplay="SHORT_NAME"}})]},
            new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("sum","NUMBER","SINGLE","metric"),new("table","TABLE","SINGLE","body")],Outputs=[]}],
            Edges=[new("n",new("s","number"),new("c","number")),new("txt",new("s","text"),new("c","text")),new("sum",new("c","sum"),new("t","sum")),new("table",new("c","table"),new("t","table"))]};
        if(returnFocus)recipe.TimeRules[0]=new(){Id="w",Mode="REPORT_SET",SourceDateBasis="REPORT_METADATA",Match="REPORTS",ReportSet=new(1,"AND",[])};
        if(approvalArrival){await TextApprovalArrivalChecks.Run(db,f,run,targets[0],sourceIds,recipe,check,ct,returnFocus);return;}
        var contexts=new List<AggregatePeriodContextDto>();var ids=new List<string>();
        foreach(var r in targets)contexts.Add((await Call("editor/bootstrap",new{reportId=r.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!);
        var configId=(await Call("configs",new AggregateConfigCreateCommandDto(contexts[0],new(run+"-scale-config",f.Binding.Id,Pin(f.TargetForm),recipe)))).GetProperty("id").GetString()!;
        for(var i=0;i<3;i++)ids.Add((await Call("instances",new AggregateInstanceCreateCommandDto(run+"-scale-instance-"+i,contexts[i],configId))).GetProperty("id").GetString()!);
        var pinReader=new AggregateMongoCommandReader(db,payload,true);
        var authority=await pinReader.AuthorizeAsync(contexts[0],f.Actor,run+"-pin-probe",ct);
        var probeInstance=(await store.ExecuteAsync((tx,token)=>tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances,ids[0],token),ct))!.Value;
        watch.Restart();var firstStamp=await pinReader.InputStampAsync(probeInstance,recipe,authority,ct);
        var pinStampMs=watch.ElapsedMilliseconds;var pinBatchQueries=pinReader.PinBatchQueryCount;
        check(pinBatchQueries<100&&authority.Pins.Select(p=>(p.Collection,p.Id)).Distinct().Count()==authority.Pins.Count,"SL2 real Mongo pin batches remain bounded and unique for 1992 reports");
        check(firstStamp==await pinReader.InputStampAsync(probeInstance,recipe,authority,ct)&&pinReader.PinBatchQueryCount==pinBatchQueries*2,"SL2 fresh second capture rereads pins and gives stable stamp without cross-request cache");
        Console.WriteLine($"MEASURE SL2 pinStampMs={pinStampMs} pinBatchQueries={pinBatchQueries} previousPerIdPinCalls={1+sourceIds.Count*7}");
        var metadataMs=new long[3];var saveMs=new long[3];var finishMs=new long[3];var final=new JsonElement[3];var samples=new List<object>();
        var change=new AggregateMappingChangeDto(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
        var started=Stopwatch.StartNew();
        // Queue sequentially, then consume concurrently with three isolated Hangfire workers.
        for(var i=0;i<3;i++){
            watch.Restart();var signed=await Call($"instances/{ids[i]}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(contexts[i],change));metadataMs[i]=watch.ElapsedMilliseconds;
            watch.Restart();var result=await Call($"instances/{ids[i]}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-scale-save-"+i,contexts[i],change,signed.GetProperty("token").GetString()!));saveMs[i]=watch.ElapsedMilliseconds;
            check(result.GetProperty("state").GetString()=="QUEUED","P05 scale save queued month "+(10+i));
        }
        var maxRunning=0;var maxReadMs=0L;var maxReadBytes=0;var maxStatusMs=0L;
        while(final.Any(x=>x.ValueKind==JsonValueKind.Undefined)){
            var running=0;
            for(var i=0;i<3;i++){
                if(final[i].ValueKind!=JsonValueKind.Undefined)continue;
                watch.Restart();await Call($"instances/{ids[i]}/computation/status",new AggregateInstanceReadCommandDto(contexts[i]));maxStatusMs=Math.Max(maxStatusMs,watch.ElapsedMilliseconds);
                watch.Restart();var read=await Call($"instances/{ids[i]}/read",new AggregateInstanceReadCommandDto(contexts[i]));maxReadMs=Math.Max(maxReadMs,watch.ElapsedMilliseconds);
                maxReadBytes=Math.Max(maxReadBytes,System.Text.Encoding.UTF8.GetByteCount(read.GetRawText()));
                var refresh=read.GetProperty("refresh");var state=refresh.GetProperty("state").GetString();
                if(state=="RUNNING")running++;
                if(state=="PENDING")await AggregateMaterializationDispatch.Schedule(db,runner,new Hangfire.BackgroundJobClient(),1,ct,f.WorkId);
                samples.Add(new{month=10+i,elapsedMs=started.ElapsedMilliseconds,refresh});
                if(state is "COMPLETED" or "FAILED" or "CANCELLED" or "DISCARDED"){
                    final[i]=read;finishMs[i]=started.ElapsedMilliseconds;Console.WriteLine($"SCALE_FINAL month={10+i} ms={finishMs[i]} state={state} error={refresh.GetProperty("errorCode")}");
                }
            }
            maxRunning=Math.Max(maxRunning,running);await Task.Delay(1000,ct);
        }
        var evidencePath=Path.Combine(AppContext.BaseDirectory,"p05-periodic-scale-"+run+".json");
        async Task Evidence(string state)=>await File.WriteAllTextAsync(evidencePath,JsonSerializer.Serialize(new{run,state,workId=f.WorkId,sourceCount=sourceIds.Count,sourceIds,targetIds=targets.Select(r=>r.Id),configId,instanceIds=ids,metadataMs,saveMs,finishMs,maxRunning,maxReadMs,maxReadBytes,maxStatusMs,pinStampMs,pinBatchQueries,seedMs,final,samples,scope="PRIVATE_HTTP_THREE_HANGFIRE_WORKERS_166_UNITS_12_MONTHS_NO_BROWSER_OR_SLA"},new JsonSerializerOptions{WriteIndented=true}),ct);
        await Evidence("MEASURED");
        var sourceAfter=await db.WorkAssignmentReports.Find(r=>r.WorkId==f.WorkId&&r.DynamicFormTemplateId==f.SourceForm.Id).ToListAsync(ct);
        check(sourceAfter.Count==units*12&&sourceAfter.All(r=>sourceHashes[r.Id]==r.PayloadHash),"P05 scale leaves all 1992 Approved source payload hashes unchanged");
        for(var i=0;i<3;i++)if(final[i].GetProperty("refresh").GetProperty("state").GetString()!="COMPLETED"){
            var unchanged=await db.WorkAssignmentReports.Find(r=>r.Id==targets[i].Id).SingleAsync(ct);
            check(unchanged.PayloadRevision==targets[i].PayloadRevision&&unchanged.PayloadHash==targets[i].PayloadHash,"P05 failed scale target keeps original payload month "+(10+i));
        }
        check(maxRunning==3,"P05 scale observed all three materializations RUNNING");
        for(var i=0;i<3;i++){
            check(final[i].GetProperty("refresh").GetProperty("state").GetString()=="COMPLETED","P05 scale background completed month "+(10+i));
            var current=await db.WorkAssignmentReports.Find(r=>r.Id==targets[i].Id).SingleAsync(ct);
            var saved=await payload.LoadReportPayloadAsync(current,ct);var fields=JsonSerializer.Deserialize<JsonElement>(saved.FieldValuesJson!);
            var month=10+i;var expected=units*(units+1)/2*month*(month+1)/2;
            check(fields.GetProperty("metric").GetDecimal()==expected&&current.PayloadRevision==targets[i].PayloadRevision+1,"P05 cumulative exact sum and one payload revision month "+month);
            var reference=JsonSerializer.Deserialize<JsonElement>(saved.TableValuesJson!).GetProperty("nativeTables").GetProperty("tables")
                .EnumerateArray().Single(t=>t.GetProperty("tableId").GetString()=="body").GetProperty("contentRef");
            var request=new AggregateContentReadRequest(current.Id,"body",current.PayloadRevision,null,reference);
            var page=await Call("content/page",request);
            check(page.GetProperty("total").GetInt32()==units*month&&page.GetProperty("rows").GetArrayLength()<=50,"P05 cumulative content cardinality and bounded page month "+month);
            var row=page.GetProperty("rows")[0];var part=await Call("content/part",request with{RowKey=row.GetProperty("rowKey").GetString()});
            check(row.GetProperty("sample").GetString()!.Length<=240&&row.GetProperty("hasMore").GetBoolean()&&part.GetProperty("text").GetString()!.Length>1000,"P05 cumulative long text teaser/full part month "+month);
        }
        var after=await db.WorkAssignmentReports.Find(r=>r.WorkId==f.WorkId&&r.DynamicFormTemplateId==f.SourceForm.Id).ToListAsync(ct);
        check(after.Count==units*12&&after.All(r=>sourceHashes[r.Id]==r.PayloadHash),"P05 scale leaves all 1992 Approved source payload hashes unchanged");
        await Evidence("PASS");Console.WriteLine("SCALE_EVIDENCE "+evidencePath);
    }
}
