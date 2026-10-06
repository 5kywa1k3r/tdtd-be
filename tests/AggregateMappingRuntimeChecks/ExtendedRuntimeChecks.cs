using System.Net;
using System.Text.Json;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class ExtendedRuntimeChecks
{
    internal static async Task Run(MongoDbContext db,AggregateMongoStore store,WorkReportPayloadService payloads,RuntimeFixture f,
        WorkAssignmentReport source2,AggregatePeriodContextDto context,AggregateRecipeDto original,string run,
        Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,IDynamicFlowDefinitionTransactionRunner runner,IConfiguration configuration,string hostRun,CancellationToken ct)
    {
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);Console.WriteLine("PASS V3 "+name);}
        Task<JsonElement> Call(string path,object body,HttpStatusCode status=HttpStatusCode.OK)=>post("aggregate-v2/"+path,body,f.Actor,status);
        var boot=await Call("editor/bootstrap",new {reportId=f.Report.Id});
        Check(boot.GetProperty("capabilities").GetProperty("extendedOperators").GetProperty("recipeVersion").GetInt32()==3,"bootstrap advertises actual v3 operators");
        var source=original.Nodes.Single(n=>n.Kind=="SOURCE");
        var projection=new[]{"score","name","unit","tags","day","active"};
        AggregateExpressionDto Merge(string mode)=>new(){Kind="LIST_MERGE",ListMerge=new(1,mode,[
            new("A",projection.Select(id=>new AggregateListProjectionDto(id,mode=="VERTICAL"?id:"left_"+id)).ToList()),
            new("B",projection.Select(id=>new AggregateListProjectionDto(id,mode=="VERTICAL"?id:"right_"+id)).ToList())])};
        AggregateExpressionDto Count(AggregateExpressionDto input,string basis)=>new(){Kind="CALL",Name="REPORT_COUNT",Arguments=[input],Options=new(){Basis=basis}};
        var fixtures=new List<object>();
        string? configId=null,instanceId=null;long revision=1,configRevision=1;
        foreach(var mode in new[]{"VERTICAL","HORIZONTAL"})
        {
            var merge=Merge(mode);
            var recipe=original with {SchemaVersion=3,SemanticProfile="REPORT_MAPPING_EXTENDED_V1",Nodes=[source,source with {Id="s2"},
                new(){Id="c",Kind="CALCULATION",Inputs=[new("A","LIST","SET"),new("B","LIST","SET")],Outputs=[new("rows","LIST","SINGLE"),new("sum","NUMBER","SINGLE")],
                    Expressions=[new("rows",merge),new("sum",Count(merge,"ELIGIBLE_SOURCES"))]},
                original.Nodes.Single(n=>n.Kind=="TARGET") with {Inputs=[new("rows","LIST","SINGLE",mode=="VERTICAL"?"items":"wide"),new("sum","NUMBER","SINGLE","total")]}],
                Edges=[new("eA",new("s","out"),new("c","A")),new("eB",new("s2","out"),new("c","B")),new("eRows",new("c","rows"),new("t","rows")),new("eSum",new("c","sum"),new("t","sum"))]};
            if(configId==null){
                var config=await Call("configs",new AggregateConfigCreateCommandDto(context,new(run+mode,context.BindingId,recipe.Nodes.Last().Form!,recipe)));
                configId=config.GetProperty("id").GetString()!;
                var instance=await Call("instances",new AggregateInstanceCreateCommandDto(run+mode+"instance",context,configId));instanceId=instance.GetProperty("id").GetString()!;
            }else{
                var impact=new AggregateConfigImpactRequestDto(configRevision,recipe,[new(instanceId!,revision)],[]);
                var prepared=await Call($"configs/{configId}/impact-preview",new AggregateConfigImpactCommandDto(context,impact));
                await Call($"configs/{configId}/revisions",new AggregateConfigRevisionCommandDto(run+mode+"revision",context,impact,prepared.GetProperty("token").GetString()!));
                configRevision++;revision++;Check(true,"common revision migrates selected draft after preview and confirmation");
            }
            var version=(await store.ExecuteAsync((tx,token)=>tx.GetAsync<AggregateConfigVersion>(AggregateCollections.Versions,AggregateCommandService.VersionKey(configId,configRevision),token),ct))!.Value;
            fixtures.Add(new {mode,workId=f.WorkId,reportId=f.Report.Id,sourceReportIds=new[]{f.Source.Id,source2.Id},configId,instanceId,recipe});
            Directory.CreateDirectory("../outputs/aggregate-operators-20261005");
            await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/fixtures-"+run+".json",JsonSerializer.Serialize(fixtures,new JsonSerializerOptions(AggregateCanonical.Json){WriteIndented=true}),ct);
            var change=new AggregateMappingChangeDto(revision,null,new([new("s","EXPLICIT_REPORTS",[f.Source.Id],[]),new("s2","EXPLICIT_REPORTS",[source2.Id],[])]),[],false);
            async Task<(string Id,JsonElement Result)> Preview(AggregateMappingChangeDto mutation)
            {
                var job=await Call("preview-jobs/start",new AggregatePreviewJobRequest("MAPPING",context,null,instanceId,mutation));var id=job.GetProperty("id").GetString()!;
                for(var i=0;i<200&&job.GetProperty("state").GetString() is "QUEUED" or "RUNNING";i++){await Task.Delay(100,ct);job=await Call($"preview-jobs/{id}/read",new{});}
                Check(job.GetProperty("state").GetString()=="COMPLETED","job completes "+mode+" "+(job.TryGetProperty("error",out var e)?e.GetRawText():""));
                return(id,job.GetProperty("result").Clone());
            }
            var preview=await Preview(change);var envelope=preview.Result.GetProperty("preview");
            var beforeCancel=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);
            var jobs=new AggregatePreviewJobs(db,payloads,configuration);
            // Separate authorized session avoids deduplicating to the already completed HTTP preview.
            var cancelled=await jobs.Start(new("MAPPING",context,null,instanceId,change),f.Actor,run+"-session",ct);
            await jobs.Cancel(cancelled,f.Actor,run+"-session",ct);
            Check(JsonSerializer.SerializeToElement(await jobs.Read(cancelled,f.Actor,run+"-session",ct),AggregateCanonical.Json).GetProperty("state").GetString()=="CANCELLED","v3 queued cancel discards result");
            var reopened=await jobs.Start(new("MAPPING",context,null,instanceId,change),f.Actor,run+"-session",ct);
            await jobs.Run(reopened,runner,payloads,ct);
            Check(JsonSerializer.SerializeToElement(await jobs.Read(reopened,f.Actor,run+"-session",ct),AggregateCanonical.Json).GetProperty("state").GetString()=="COMPLETED","v3 reopened job uses current authorized pins");
            var afterCancel=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);
            Check(afterCancel.PayloadRevision==beforeCancel.PayloadRevision&&afterCancel.PayloadHash==beforeCancel.PayloadHash,"v3 preview cancel reopen never writes report");
            var results=envelope.GetProperty("preview").GetProperty("results");var reference=results[0].GetProperty("value");
            Check(reference.GetProperty("kind").GetString()=="LIST_RECORDS_V2"&&reference.GetProperty("count").GetInt32()==(mode=="VERTICAL"?5:3),"composite snapshot count "+mode);
            Check(results[1].GetProperty("value").GetString()=="2","eligible report count is2 independently of elements "+mode);
            var query=new AggregateListPageRequestDto(f.Report.Id,preview.Id,reference.GetProperty("id").GetString()!,reference.GetProperty("hash").GetString()!,Offset:0,Limit:2);
            var page=await Call("lists/page",query);
            Check(page.GetProperty("items").GetArrayLength()==2&&page.GetRawText().Length<6000,"bounded page excludes full text and provenance "+mode);
            var rowKey=page.GetProperty("items")[0].GetProperty("key").GetString();var textField=mode=="VERTICAL"?"name":"right_name";
            var detail=await Call("lists/detail",query with {RowKey=rowKey,FieldId=textField,IncludeLineage=true});
            Check(detail.GetProperty("cell").GetProperty("value").GetString()!.Length>8000&&detail.GetProperty("contributors").GetArrayLength()==(mode=="VERTICAL"?1:2),"full text detail and multi-origin lineage "+mode);
            Check(detail.GetProperty("cell").GetProperty("lineage")[0].GetProperty("fieldId").GetString()=="name","projected cell preserves source fieldId "+mode);
            var choice=await Call("lists/detail",query with {RowKey=rowKey,FieldId=mode=="VERTICAL"?"unit":"right_unit",IncludeLineage=true});
            Check(choice.GetProperty("cell").GetProperty("displayOptions")[0].GetProperty("label").GetString()=="Đơn vị công tác A","choice label uses source pin per cell "+mode);
            if(mode=="HORIZONTAL")
            {
                var last=await Call("lists/page",query with {Offset=2,Limit=1});
                Check(last.GetProperty("items")[0].GetProperty("cells").GetProperty("left_score").GetProperty("state").GetString()=="BLANK","missing counterpart remains blank");
                Check(envelope.GetProperty("listOperations")[0].GetProperty("mergeInputs")[0].GetProperty("missingRows").GetInt32()==1,"specific missing-side warning");
            }
            await Call($"instances/{instanceId}/apply",new AggregateMappingApplyCommandDto(run+mode+"apply",context,change,preview.Result.GetProperty("token").GetString()!));
            revision++;change=change with {ExpectedRevision=revision};
            var report=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);var saved=await payloads.LoadReportPayloadAsync(report,ct);
            var native=JsonDocument.Parse(saved.TableValuesJson!).RootElement.GetProperty("nativeTables").GetProperty("tables").EnumerateArray().Single(t=>t.GetProperty("tableId").GetString()==(mode=="VERTICAL"?"items":"wide")).GetProperty("records");
            Check(native.GetArrayLength()==(mode=="VERTICAL"?5:3)&&native.EnumerateArray().All(r=>Guid.TryParse(r.GetProperty("recordId").GetString(),out _)),"authoritative native writer UUID roundtrip "+mode);
            var read=await Call($"instances/{instanceId}/read",new AggregateInstanceReadCommandDto(context));
            var readPage=await Call("lists/page",query with {JobId=null,ReadId=read.GetProperty("listReadId").GetString()});Check(readPage.GetProperty("total").GetInt32()==native.GetArrayLength(),"saved snapshot read without preview job "+mode);
            var staleApply=await Call($"instances/{instanceId}/apply",new AggregateMappingApplyCommandDto(run+mode+"stale",context,change with {ExpectedRevision=revision-1},preview.Result.GetProperty("token").GetString()!),HttpStatusCode.Conflict);
            Check(staleApply.GetProperty("code").GetString()=="AGG_REVISION_CONFLICT","v3 token rejects revision changed after preview: "+staleApply.GetProperty("code").GetString());
            var revoke=await db.WorkTemplateAssignees.UpdateOneAsync(b=>b.Id==f.Binding.Id&&b.AssigneeUserId==f.Actor,Builders<WorkTemplateAssignee>.Update.Set(b=>b.AssigneeUserId,f.Outsider),cancellationToken:ct);
            Check(revoke.MatchedCount==1,"revoke only fixture owner");
            try {await Call("lists/page",query,HttpStatusCode.NotFound);Check(true,"v3 old page grant rechecks report authority");}
            finally {await db.WorkTemplateAssignees.UpdateOneAsync(b=>b.Id==f.Binding.Id&&b.AssigneeUserId==f.Outsider,Builders<WorkTemplateAssignee>.Update.Set(b=>b.AssigneeUserId,f.Actor),cancellationToken:CancellationToken.None);}
            if(mode=="VERTICAL")
            {
                var weighted=new AggregateExpressionDto {Kind="LIST_PIPELINE",Ref="A",ListPipeline=new(){Version=2,Sort=[],Scope="ALL_SOURCES",Take="ALL",Project=[new("score","score")],Operation="WEIGHTED_AVG",ValueFieldId="score",WeightFieldId="score"}};
                var weightedPreview=await Preview(change with {Overrides=new(version.RecipeHash,[],[new("c","sum",weighted)],[])});
                Check(weightedPreview.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[1].GetProperty("value").GetString()=="10.714286","weighted average exact rounding six decimals over same row");
                // Reject changes in real source status after preview; always restore only this fixture's transition.
                await db.WorkAssignmentReports.UpdateOneAsync(r=>r.Id==f.Source.Id,Builders<WorkAssignmentReport>.Update.Set(r=>r.IsCurrent,false).Inc(r=>r.LifecycleRevision,1),cancellationToken:ct);
                try{await Call("lists/page",query with {JobId=weightedPreview.Id},HttpStatusCode.Conflict);Check(true,"stale current source rejects page");}
                finally{await db.WorkAssignmentReports.UpdateOneAsync(r=>r.Id==f.Source.Id&&!r.IsCurrent,Builders<WorkAssignmentReport>.Update.Set(r=>r.IsCurrent,true).Inc(r=>r.LifecycleRevision,1),cancellationToken:CancellationToken.None);}
            }
        }
        var submission=await Call($"reports/{f.Report.Id}/submission-preview",new AggregateInstanceReadCommandDto(context));
        await LifecycleChecks.Run(db,runner,configuration,f,hostRun,instanceId!,submission.GetProperty("token").GetString()!,ct,async ()=>{
            var frozen=await Call($"instances/{instanceId}/read",new AggregateInstanceReadCommandDto(context));
            var reference=frozen.GetProperty("lastResults")[0].GetProperty("value");
            var frozenPage=await Call("lists/page",new AggregateListPageRequestDto(f.Report.Id,null,reference.GetProperty("id").GetString()!,reference.GetProperty("hash").GetString()!,ReadId:frozen.GetProperty("listReadId").GetString()));
            Check(reference.GetProperty("kind").GetString()=="LIST_RECORDS_V2"&&frozenPage.GetProperty("total").GetInt32()==3,"v3 frozen composite snapshot remains readable");
        });
        Directory.CreateDirectory("../outputs/aggregate-operators-20261005");
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/fixtures-"+run+".json",JsonSerializer.Serialize(fixtures,new JsonSerializerOptions(AggregateCanonical.Json){WriteIndented=true}),ct);
    }
}
