using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Bson.Serialization;
using Minio;
using Minio.DataModel.Args;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class ContentTableApiChecks
{
    internal static async Task Run(MongoDbContext db, AggregateMongoStore store, IDynamicFlowDefinitionTransactionRunner runner,
        IConfiguration config, string run, Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post, InjectedPayloadWriter faultWriter, CancellationToken ct, MinioUploadFault? uploadFault = null)
    {
        void Check(bool good,string label){if(!good)throw new Exception(label);Console.WriteLine("PASS content-api "+label);}
        var table=new DynamicFormNativeTableDto { Id="body",SectionId="main",Name="Nội dung",Layout="vertical",Order=0,
            Fields=[new(){Id="unit",Name="Đơn vị báo cáo",Order=0},new(){Id="content",Name="Nội dung",Order=1}],Rows=[],StatisticTargets=[],
            TypeConfig=new(){Version=1,Sequence=2,Rules=[new(){Order=1,Target=new(){Scope="column",FieldId="unit"},Spec=new(){Type="plainText",Required=false}},
                new(){Order=2,Target=new(){Scope="column",FieldId="content"},Spec=new(){Type="plainText",Required=false}}]}};
        var f=await RuntimeFixture.Seed(db,run+"-content",ct,configureForm:(form,target)=>{
            if(target){form.NativeTablesVersion=1;form.TablesJson=JsonSerializer.Serialize(new[]{table},AggregateCanonical.Json);}
            else form.FieldsJson="[{\"id\":\"n\",\"key\":\"n\",\"name\":\"Nội dung\",\"label\":\"Nội dung\",\"sectionId\":\"main\",\"type\":\"longText\",\"required\":false}]";
        });
        await FixtureProvenanceChecks.Migration(db,"content after seed",ct);
        var payload=new WorkReportPayloadService(db);
        Task<JsonElement> Call(string path,object body)=>post("aggregate-v2/"+path,body,f.Actor,HttpStatusCode.OK);
        var text=new string('x',16383)+"📝\n"+string.Concat(Enumerable.Repeat("Nội dung rất dài, tiếng Việt.\n",2000));
        async Task Write(WorkAssignmentReport report,DynamicFormTemplate form,string? content){
            report.DynamicFormSchemaHash=form.PublishedSchemaHash;
            var tables=report.Id==f.Report.Id?JsonSerializer.Serialize(new{nativeTables=new{version=1,schemaHash=form.PublishedSchemaHash,tables=new[]{new{tableId="body",records=Array.Empty<object>()}}}},AggregateCanonical.Json):null;
            var saved=await payload.SaveReportPayloadAsync(report,"[]",report.Id==f.Report.Id?"{}":JsonSerializer.Serialize(new{n=content}),tables,null,f.Actor,DateTime.UtcNow,ct);
            report.PayloadRevision=saved.PayloadRevision;report.PayloadHash=saved.PayloadHash;report.PayloadSizeBytes=saved.PayloadSizeBytes;report.PayloadStatus=saved.PayloadStatus;
            await db.WorkAssignmentReports.ReplaceOneAsync(x=>x.Id==report.Id,report,cancellationToken:ct);
        }
        await Write(f.Source,f.SourceForm,text);await Write(f.Report,f.TargetForm,null);
        foreach(var report in new[]{f.Source,f.Report})await store.ExecuteAsync(async(tx,cancel)=>{
            foreach(var pair in new[]{("REPORT",report.Id),("SLOT",report.PeriodInstanceKey!)})await tx.PutAsync(AggregateCollections.Declarations,pair.Item1+":"+pair.Item2,0,
                new AggregateDeclarationState(pair.Item1,pair.Item2,f.WorkId,report.WorkAssignmentId,new("2026-09-01","2026-09-30","USER_DECLARED",run,1)),f.WorkId,pair.Item2,[],cancel);
            return true;},ct);
        var originalAssignment=await db.WorkAssignments.Find(x=>x.Id==f.Source.WorkAssignmentId).SingleAsync(ct);
        var originalPeriod=await db.WorkReportPeriods.Find(x=>x.Id==f.Source.WorkReportPeriodId).SingleAsync(ct);
        var originalBinding=await db.WorkTemplateAssignees.Find(x=>x.Id==originalPeriod.WorkTemplateAssigneeId).SingleAsync(ct);
        T Clone<T>(T value)=>BsonSerializer.Deserialize<T>(value!.ToBson());
        string Id()=>ObjectId.GenerateNewId().ToString();
        for(var i=1;i<166;i++){
            var user=Id();var unit=Id();
            await db.Users.InsertOneAsync(new AppUser{Id=user,Username=run+"-content-"+i,UnitId=unit,CreatedByUserId=f.Actor},cancellationToken:ct);
            var assignment=Clone(originalAssignment);assignment.Id=Id();assignment.Code=run+"-content-"+i;assignment.Path="/"+f.Binding.WorkAssignmentId+"/"+assignment.Id;
            assignment.Assignees=[new(){UserId=user,UnitId=unit}];assignment.TargetUnitIds=[unit];
            var binding=Clone(originalBinding);binding.Id=Id();binding.WorkAssignmentId=assignment.Id;binding.AssigneeUserId=user;binding.AssigneeUnitId=unit;
            var period=Clone(originalPeriod);period.Id=Id();period.WorkAssignmentId=assignment.Id;period.WorkTemplateAssigneeId=binding.Id;period.AssigneeUserId=user;period.AssigneeUnitId=unit;period.PeriodInstanceKey=binding.Id+":ONCE";
            var source=Clone(f.Source);source.Id=Id();source.WorkAssignmentId=assignment.Id;source.WorkReportPeriodId=period.Id;source.PeriodInstanceKey=period.PeriodInstanceKey;source.AssigneeUserId=user;
            source.PayloadRevision=0;source.PayloadHash=null;source.PayloadSizeBytes=0;period.CurrentReportId=source.Id;
            await db.WorkAssignments.InsertOneAsync(assignment,cancellationToken:ct);await db.WorkTemplateAssignees.InsertOneAsync(binding,cancellationToken:ct);await db.WorkReportPeriods.InsertOneAsync(period,cancellationToken:ct);
            await Write(source,f.SourceForm,text+i);await db.WorkAssignmentReports.InsertOneAsync(source,cancellationToken:ct);
            await store.ExecuteAsync(async(tx,cancel)=>{foreach(var pair in new[]{("REPORT",source.Id),("SLOT",source.PeriodInstanceKey!)})await tx.PutAsync(AggregateCollections.Declarations,pair.Item1+":"+pair.Item2,0,
                new AggregateDeclarationState(pair.Item1,pair.Item2,f.WorkId,source.WorkAssignmentId,new("2026-09-01","2026-09-30","USER_DECLARED",run,1)),f.WorkId,pair.Item2,[],cancel);return true;},ct);
        }
        Console.WriteLine($"MEASURE content-api sources=166 charactersPerSource={text.Length}+suffix");
        var boot=await Call("editor/bootstrap",new{reportId=f.Report.Id});var context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var recipe=new AggregateRecipeDto {SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",Nodes=[
            new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("out","TEXT","SET","n","w")]},
            new(){Id="c",Kind="CALCULATION",Inputs=[new("in","TEXT","SET")],Outputs=[new("out","TABLE","SINGLE")],Expressions=[new("out",new(){Kind="CALL",Name="REPORT_TEXT_TABLE",Arguments=[new(){Kind="INPUT",Ref="in"}],Options=new(){Order="UNIT_THEN_PERIOD"}})]},
            new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("in","TABLE","SINGLE","body")],Outputs=[]}],
            Edges=[new("a",new("s","out"),new("c","in")),new("b",new("c","out"),new("t","in"))],
            TimeRules=[new(){Id="w",Mode="TARGET_DATA_WINDOW",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED"}]};
        var cfg=await Call("configs",new AggregateConfigCreateCommandDto(context,new(run+"-content-config",context.BindingId,Pin(f.TargetForm),recipe)));
        var instance=await Call("instances",new AggregateInstanceCreateCommandDto(run+"-content-instance",context,cfg.GetProperty("id").GetString()!));var instanceId=instance.GetProperty("id").GetString()!;
        var change=new AggregateMappingChangeDto(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var result=await Call("preview-jobs/start",new AggregatePreviewJobRequest("MAPPING",context,null,instanceId,change));
        var job=result.GetProperty("id").GetString()!;
        while(result.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){
            await Task.Delay(200,ct);result=await Call($"preview-jobs/{job}/read",new{});
        }
        Console.WriteLine($"MEASURE content-api jobAndReadMs={watch.ElapsedMilliseconds}");
        Check(result.GetProperty("state").GetString()=="COMPLETED","real Hangfire preview job completes: "+result.GetProperty("errorCode"));
        var prepared=result.GetProperty("result");var reference=prepared.GetProperty("preview").GetProperty("preview").GetProperty("results")[0].GetProperty("value");
        var request=new AggregateContentReadRequest(f.Report.Id,null,null,job,reference);
        var page=await Call("content/page",request);Check(page.GetProperty("total").GetInt32()==166&&page.GetProperty("rows").GetArrayLength()==20,"preview page authorized through completed job");
        Check(!result.GetRawText().Contains(text[..1000]),"preview carries small content references instead of duplicated full text");
        Console.WriteLine($"MEASURE content-api previewJsonCharacters={result.GetRawText().Length}");
        await post("aggregate-v2/content/page",request,f.Outsider,HttpStatusCode.NotFound);
        await post("aggregate-v2/content/page",request with{ReportId=f.Source.Id},f.Actor,HttpStatusCode.NotFound);
        Check(true,"other actor and wrong report cannot read job content");
        var row=page.GetProperty("rows")[0];var rowKey=row.GetProperty("rowKey").GetString()!;var restored=new System.Text.StringBuilder();int? part=0;
        while(part!=null){var body=await Call("content/part",request with{RowKey=rowKey,Part=part.Value});restored.Append(body.GetProperty("text").GetString());part=body.GetProperty("nextPart").ValueKind==JsonValueKind.Null?null:body.GetProperty("nextPart").GetInt32();}
        Check(restored.ToString()==text,"HTTP parts restore exact long Unicode content");
        var before=(await db.WorkAssignmentReports.Find(x=>x.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision;
        Check(before==f.Report.PayloadRevision,"preview leaves target revision untouched");
        var apply=new AggregateMappingApplyCommandDto(run+"-content-apply",context,change,prepared.GetProperty("token").GetString()!);
        faultWriter.FailAfterWrite=true;
        try{await post($"aggregate-v2/instances/{instanceId}/apply",apply,f.Actor,HttpStatusCode.InternalServerError);}
        finally{faultWriter.FailAfterWrite=false;}
        Check(await db.Db.GetCollection<BsonDocument>(AggregateCollections.NativeContent).CountDocumentsAsync(new BsonDocument("target",f.Report.Id),cancellationToken:ct)==0
            && (await db.WorkAssignmentReports.Find(x=>x.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==before,"failed native write rolls back binding and payload together");
        uploadFault?.Arm();
        watch.Restart();await Call($"instances/{instanceId}/apply",apply);Console.WriteLine($"MEASURE content-api applyMs={watch.ElapsedMilliseconds}");
        var savedReport=await db.WorkAssignmentReports.Find(x=>x.Id==f.Report.Id).SingleAsync(ct);var snapshot=await payload.LoadReportPayloadAsync(savedReport,ct);
        DynamicFormNativeTableValues.Validate(f.TargetForm,savedReport.DynamicFormSchemaHash,snapshot.TableValuesJson,false);
        var native=JsonSerializer.Deserialize<JsonElement>(snapshot.TableValuesJson!).GetProperty("nativeTables").GetProperty("tables")[0];
        Check(native.GetProperty("contentRef").GetRawText()==reference.GetRawText()&&native.GetProperty("records").GetArrayLength()==0,"native Apply stores sealed real table descriptor");
        var savedRequest=request with{JobId=null,TableId="body",PayloadRevision=savedReport.PayloadRevision};
        Check((await Call("content/page",savedRequest)).GetProperty("total").GetInt32()==166,"saved native table reads through report policy");
        var snapshotRows=db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots);
        var intent=await snapshotRows.Find(new BsonDocument("target",f.Report.Id)).SingleAsync(ct);
        Check(intent.Contains("body"),"Apply commits durable snapshot intent with native payload");
        if (uploadFault != null)
        {
            watch.Restart();
            while (uploadFault.Failures == 0 && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100, ct);
            var afterFailure = await snapshotRows.Find(new BsonDocument("_id", intent["_id"])).SingleAsync(ct);
            Check(uploadFault.Failures == 1 && afterFailure.GetValue("state", "PENDING") != "READY", "injected upload failure leaves durable intent pending, never falsely READY");
            Check((await db.WorkAssignmentReports.Find(x => x.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision == savedReport.PayloadRevision,
                "MinIO failure after commit leaves the saved report revision unchanged");
            Check((await Call("content/page", savedRequest)).GetProperty("total").GetInt32() == 166,
                "saved table stays readable while archive upload is pending");
        }
        var storageConfig=new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build();
        using var minio=new MinioClient().WithEndpoint(storageConfig["Minio:Endpoint"]!).WithCredentials(storageConfig["Minio:AccessKey"]!,storageConfig["Minio:SecretKey"]!)
            .WithSSL(storageConfig.GetValue<bool>("Minio:Secure")).Build();
        config["Uploads:Bucket"]=storageConfig["Uploads:Bucket"]??"tdtd-attachments";
        watch.Restart();var archived=intent;
        while(archived.GetValue("state","")!="READY"&&watch.Elapsed<TimeSpan.FromSeconds(uploadFault == null ? 30 : 90)){
            await Task.Delay(200,ct);archived=await snapshotRows.Find(new BsonDocument("_id",intent["_id"])).SingleAsync(ct);
        }
        Console.WriteLine($"MEASURE content-api archiveWaitMs={watch.ElapsedMilliseconds}");
        Check(archived.GetValue("state","")=="READY","real Hangfire dispatch uploads MinIO snapshot after native commit");
        if (uploadFault != null) Check(uploadFault.Failures == 1
            && (await db.WorkAssignmentReports.Find(x => x.Id == f.Report.Id).SingleAsync(ct)).PayloadRevision == savedReport.PayloadRevision
            && await snapshotRows.CountDocumentsAsync(new BsonDocument("target", f.Report.Id), cancellationToken: ct) == 1,
            "real Hangfire retry recovers the single upload fault without reapplying report payload or duplicating snapshot intent");
        await using var archive=new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs().WithBucket(archived["bucket"].AsString).WithObject(archived["objectKey"].AsString).WithCallbackStream(stream=>stream.CopyTo(archive)),ct);
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive.ToArray()))==archived["sha256"].AsString,"MinIO readback matches archived file hash");
        await post("aggregate-v2/content/page",savedRequest with{PayloadRevision=before},f.Actor,HttpStatusCode.Conflict);
        var fake=snapshot.TableValuesJson!.Replace(reference.GetProperty("id").GetString()!,new string('A',64));
        try{await payload.SaveReportPayloadAsync(savedReport,"[]","{}",fake,null,f.Actor,DateTime.UtcNow,ct);throw new Exception("forged ref accepted");}
        catch(AggregatePreviewException ex){Check(ex.Code=="AGG_CONTENT_FORBIDDEN","common writer rejects forged content reference");}
        var values=AggregateNativePayloadAdapter.Read(f.TargetForm,snapshot,AggregateNativePayloadAdapter.Schema(f.TargetForm),null);
        Check(values["body"].ContentReference!=null&&values["body"].Table==null,"previous target retains reference rather than false empty table");
        // A blank approved source still contributes a row when recomputed.
        await Write(f.Source,f.SourceForm,null);
        boot=await Call("editor/bootstrap",new{reportId=f.Report.Id});context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        change=change with{ExpectedRevision=2};
        var blank=await Call($"instances/{instanceId}/mapping/preview",new AggregateMappingPreviewCommandDto(context,change));
        var blankRef=blank.GetProperty("preview").GetProperty("preview").GetProperty("results")[0].GetProperty("value").Deserialize<AggregateContentReference>(AggregateCanonical.Json)!;
        var blankPage=await new AggregateContentTableStore(db).Page(f.Report.Id,blankRef,0,20,ct);
        Check(blankPage.Total==166&&blankPage.Rows[0].State=="BLANK"&&blankPage.Rows[0].Sample=="","approved blank source retains its row among 166 units");
        Console.WriteLine("PASS content-api slice; local isolated host, real Hangfire preview/archive delivery and MinIO; "
            + (uploadFault == null ? "no retry-fault claim" : "single upload failure and automatic retry verified") + "; no browser claim; fixtures retained.");
    }
}
