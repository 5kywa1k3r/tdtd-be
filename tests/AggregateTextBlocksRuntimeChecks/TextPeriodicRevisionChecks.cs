using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextPeriodicRevisionChecks
{
    internal static async Task Run(MongoDbContext db,AggregateMongoStore store,WorkReportPayloadService payloads,
        RuntimeFixture f,string run,AggregateRecipeDto original,string configId,List<string> instanceIds,
        IReadOnlyList<WorkAssignmentReport> targets,IReadOnlyList<WorkAssignmentReport> sources,
        IReadOnlyDictionary<string,string> blocks,Func<string,object,string?,HttpStatusCode,Task<JsonElement>> post,
        Action<bool,string> check,CancellationToken ct)
    {
        Task<JsonElement> Call(string path,object body,HttpStatusCode status=HttpStatusCode.OK)=>post(path,body,f.Actor,status);
        async Task<JsonElement> Boot(int index)=>await Call("editor/bootstrap",new{reportId=targets[index].Id});
        async Task<AggregatePeriodContextDto> Context(int index)=>(await Boot(index)).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        async Task<AggregateInstanceState> Instance(int index)=>AggregateMongoTransaction.Read<AggregateInstanceState>(
            await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceIds[index])).SingleAsync(ct)).Value;
        Task<WorkAssignmentReport> Target(int index)=>db.WorkAssignmentReports.Find(r=>r.Id==targets[index].Id).SingleAsync(ct);
        async Task<JsonElement> Fields(int index)=>JsonSerializer.Deserialize<JsonElement>((await payloads.LoadReportPayloadAsync(await Target(index),ct)).FieldValuesJson!);
        var selection=new AggregateInstanceSelectionDto([new("s","FORM_SELECTOR",[],[])]);
        var artifactJobs=new List<object>();
        AggregateRecipeDto Window(string mode,string? start=null,string? end=null,AggregateReportFilterDto? filter=null)
            =>original with{TimeRules=[original.TimeRules.Single() with{Mode=mode,StartDate=start,EndDate=end,ReportFilter=filter}]};
        async Task<JsonElement> Raw(int index,AggregateRecipeDto graph){
            var boot=await Boot(index);var context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            var expected=boot.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
            var request=new AggregatePreviewRequestDto(context,"periodic-time-probe","periodic-time-probe-config",expected,graph,selection);
            var job=await Call("preview-jobs/start",new AggregatePreviewJobRequest("RECIPE",context,request,null,null));
            while(job.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){
                await Task.Delay(150,ct);job=await Call("preview-jobs/"+job.GetProperty("id").GetString()+"/read",new{});
            }
            check(job.GetProperty("state").GetString()=="COMPLETED","P05 periodic raw job completes "+graph.TimeRules[0].Mode+": "+job.GetProperty("errorCode"));
            artifactJobs.Add(new{kind="TIME_RULE",targetReportId=targets[index].Id,mode=graph.TimeRules[0].Mode,jobId=job.GetProperty("id").GetString()});
            return job.GetProperty("result").GetProperty("preview");
        }
        JsonElement Result(JsonElement preview,string port)=>preview.GetProperty("results").EnumerateArray().Single(r=>r.GetProperty("portId").GetString()==port);
        string Joined(params int[] indices)=>string.Join("\n\n",indices.Select(i=>blocks[sources[i].Id]));
        bool SourceIds(JsonElement preview,params int[] indices)=>preview.GetProperty("contributingSources").EnumerateArray()
            .Select(p=>p.GetProperty("reportId").GetString()).Order().SequenceEqual(indices.Select(i=>sources[i].Id).Order());
        var beforeTime=new[]{await Target(0),await Target(1),await Target(2)};
        var cumulative=Window("CUMULATIVE_FROM","2026-09-01");
        var september=await Raw(0,cumulative);var october=await Raw(1,cumulative);
        check(SourceIds(september,0)&&Result(september,"plain").GetProperty("value").GetString()==Joined(0)
            &&september.GetProperty("windows")[0].GetProperty("endDate").GetString()=="2026-09-30",
            "P05 cumulative September excludes later Approved months");
        check(SourceIds(october,0,1)&&Result(october,"plain").GetProperty("value").GetString()==Joined(0,1)
            &&october.GetProperty("windows")[0].GetProperty("endDate").GetString()=="2026-10-31",
            "P05 cumulative October reads September and October sources, not a parent aggregate snapshot");
        var fixedSeptember=Window("EXPLICIT_RANGE","2026-09-01","2026-09-30");
        var fixedOctoberTarget=await Raw(1,fixedSeptember);var fixedNovemberTarget=await Raw(2,fixedSeptember);
        check(SourceIds(fixedOctoberTarget,0)&&SourceIds(fixedNovemberTarget,0)
            &&Result(fixedNovemberTarget,"plain").GetProperty("value").GetString()==Joined(0),
            "P05 fixed September remains fixed when target changes to October or November");
        var explicitNext=await Raw(0,Window("EXPLICIT_RANGE","2026-10-01","2026-10-31"));
        check(SourceIds(explicitNext,1),"P05 explicit October source is selectable from September target when current Approved and readable");
        var contradictory=await Raw(1,Window("TARGET_DATA_WINDOW",filter:new(PeriodKeys:["20260930"])));
        check(SourceIds(contradictory)&&Result(contradictory,"plain").GetProperty("state").GetString()=="NO_RESULT"
            &&Result(contradictory,"plain").GetProperty("value").ValueKind==JsonValueKind.Null,
            "P05 fixed source period filter intersects target window; it does not imply previous-period offset or zero");
        for(var i=0;i<3;i++){
            var after=await Target(i);
            check(after.PayloadRevision==beforeTime[i].PayloadRevision&&after.PayloadHash==beforeTime[i].PayloadHash,
                "P05 time-rule previews do not write period "+targets[i].PeriodKey);
        }

        // A private October override stays pinned to r1, without cloning a Form.
        var octoberContext=await Context(1);var octoberInstance=await Instance(1);
        var overrideRule=cumulative.TimeRules.Single();
        var overrideDto=new AggregateInstanceOverrideDto(AggregateCanonical.Hash(original),[overrideRule],[],[]);
        var overrideChange=new AggregateMappingChangeDto(octoberInstance.Revision,overrideDto,selection,[],false);
        var overridePreview=await Call("instances/"+instanceIds[1]+"/mapping/preview",new AggregateMappingPreviewCommandDto(octoberContext,overrideChange));
        check(overridePreview.GetProperty("preview").GetProperty("preview").GetProperty("contributingSources").GetArrayLength()==2,
            "P05 per-period cumulative override previews both months");
        await Call("instances/"+instanceIds[1]+"/apply",new AggregateMappingApplyCommandDto(run+"-october-cumulative-override",octoberContext,
            overrideChange,overridePreview.GetProperty("token").GetString()!));
        octoberInstance=await Instance(1);var octoberOverrideReport=await Target(1);
        check(octoberInstance.ConfigRevision==1&&octoberInstance.Overrides?.TimeRules.Single().Mode=="CUMULATIVE_FROM"
            &&(await Fields(1)).GetProperty("plain").GetString()==Joined(0,1),
            "P05 applied period override remains r1 with native readback and no new Form");

        // r2 adds a NUMBER result and uses the same approved source-window mechanism.
        var source=original.Nodes.Single(n=>n.Kind=="SOURCE");var calculation=original.Nodes.Single(n=>n.Kind=="CALCULATION");
        var target=original.Nodes.Single(n=>n.Kind=="TARGET");
        var r2=cumulative with{Nodes=[source with{Outputs=[..source.Outputs,new("score","NUMBER","SET","score","w")]},
            calculation with{Inputs=[..calculation.Inputs,new("score","NUMBER","SET")],Outputs=[..calculation.Outputs,new("sum","NUMBER","SINGLE")],
                Expressions=[..calculation.Expressions!,new("sum",new(){Kind="CALL",Name="SUM",Arguments=[new(){Kind="INPUT",Ref="score"}]})]},
            target with{Inputs=[..target.Inputs,new("metric","NUMBER","SINGLE","metric")]}],
            Edges=[..original.Edges,new("score-in",new("s","score"),new("c","score")),new("score-result",new("c","sum"),new("t","metric"))]};
        var septemberContext=await Context(0);var firstInstance=await Instance(0);
        var impact=new AggregateConfigImpactRequestDto(1,r2,[new(instanceIds[0],firstInstance.Revision)],[]);
        var septemberBefore=await Target(0);
        var impactPreview=await Call("configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(septemberContext,impact));
        check(impactPreview.GetProperty("token").ValueKind==JsonValueKind.String&&impactPreview.GetProperty("plan").GetProperty("conflicts").GetArrayLength()==0,
            "P05 common r2 impact has no conflict for selected September draft");
        check((await Target(0)).PayloadHash==septemberBefore.PayloadHash&&(await Target(1)).PayloadHash==octoberOverrideReport.PayloadHash,
            "P05 common impact preview does not write selected or unselected drafts");
        await Call("configs/"+configId+"/revisions",new AggregateConfigRevisionCommandDto(run+"-common-r2",septemberContext,impact,impactPreview.GetProperty("token").GetString()!));
        var migratedSeptember=await Instance(0);var unchangedOctober=await Instance(1);
        check(migratedSeptember.ConfigRevision==2&&(await Fields(0)).GetProperty("metric").GetRawText()=="10",
            "P05 selected September draft migrates to r2 and recomputes native numeric payload");
        check(unchangedOctober.ConfigRevision==1&&unchangedOctober.Revision==octoberInstance.Revision
            &&(await Target(1)).PayloadRevision==octoberOverrideReport.PayloadRevision&&(await Target(1)).PayloadHash==octoberOverrideReport.PayloadHash
            &&unchangedOctober.Overrides?.BaseRecipeHash==AggregateCanonical.Hash(original),
            "P05 unselected October retains r1/override/payload exactly when common head becomes r2");

        var novemberContext=await Context(2);
        var nextInstance=await Call("instances",new AggregateInstanceCreateCommandDto(run+"-november-after-r2",novemberContext,configId));
        instanceIds.Add(nextInstance.GetProperty("id").GetString()!);
        var novemberInstance=await Instance(2);
        check(novemberInstance.ConfigRevision==2&&novemberInstance.Overrides==null,"P05 newly opened November instance uses common head r2");
        var novemberChange=new AggregateMappingChangeDto(novemberInstance.Revision,null,selection,[],false);
        var novemberPreview=await Call("instances/"+instanceIds[2]+"/mapping/preview",new AggregateMappingPreviewCommandDto(novemberContext,novemberChange));
        check(novemberPreview.GetProperty("preview").GetProperty("preview").GetProperty("contributingSources").GetArrayLength()==3,
            "P05 November r2 cumulative preview links three monthly reports");
        await Call("instances/"+instanceIds[2]+"/apply",new AggregateMappingApplyCommandDto(run+"-november-r2-apply",novemberContext,novemberChange,novemberPreview.GetProperty("token").GetString()!));
        var novemberFields=await Fields(2);
        check(novemberFields.GetProperty("metric").GetRawText()=="60"&&novemberFields.GetProperty("plain").GetString()==Joined(0,1,2)
            &&novemberFields.GetProperty("total").GetArrayLength()==1,
            "P05 cumulative SUM 10+20+30=60 and whole report blocks survive native Apply/readback");

        // r3 changes the base time rule that October has overridden: explicit conflict choices.
        var r3=r2 with{TimeRules=[original.TimeRules.Single() with{Mode="EXPLICIT_RANGE",StartDate="2026-10-01",EndDate="2026-10-31"}]};
        octoberContext=await Context(1);octoberInstance=await Instance(1);
        var conflicted=new AggregateConfigImpactRequestDto(2,r3,[new(instanceIds[1],octoberInstance.Revision)],[]);
        var conflictPreview=await Call("configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(octoberContext,conflicted));
        var conflict=conflictPreview.GetProperty("plan").GetProperty("conflicts")[0];
        check(conflictPreview.GetProperty("token").ValueKind==JsonValueKind.Null&&conflict.GetProperty("timeRuleId").GetString()=="w"
            &&conflict.GetProperty("allowedResolutions").EnumerateArray().Select(v=>v.GetString()).Order().SequenceEqual(new[]{"KEEP_OVERRIDE","USE_NEW_BASE"}),
            "P05 r3 reports time-rule override conflict with no confirmation token");
        var conflictRejected=await Call("configs/"+configId+"/revisions",new AggregateConfigRevisionCommandDto(run+"-r3-unresolved",octoberContext,conflicted,"invalid-token"),HttpStatusCode.Conflict);
        check(conflictRejected.GetProperty("code").GetString()=="AGG_REBASE_REQUIRED"&&(await Target(1)).PayloadHash==octoberOverrideReport.PayloadHash,
            "P05 unresolved revision conflict blocks commit without changing period payload");
        var keep=conflicted with{Resolutions=[new(instanceIds[1],"",null,"w","KEEP_OVERRIDE")]};
        var useBase=conflicted with{Resolutions=[new(instanceIds[1],"",null,"w","USE_NEW_BASE")]};
        var keepPreview=await Call("configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(octoberContext,keep));
        var basePreview=await Call("configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(octoberContext,useBase));
        JsonElement ImpactResult(JsonElement prepared)=>prepared.GetProperty("plan").GetProperty("previews").GetProperty(instanceIds[1]).GetProperty("preview");
        check(keepPreview.GetProperty("token").ValueKind==JsonValueKind.String&&Result(ImpactResult(keepPreview),"metric").GetProperty("value").GetString()=="30"
            &&Result(ImpactResult(basePreview),"metric").GetProperty("value").GetString()=="20",
            "P05 keep override previews cumulative30; use new base previews October20 before any commit");
        var septemberR2Report=await Target(0);var novemberR2Report=await Target(2);
        await Call("configs/"+configId+"/revisions",new AggregateConfigRevisionCommandDto(run+"-common-r3-keep-override",octoberContext,keep,keepPreview.GetProperty("token").GetString()!));
        var octoberR3=await Instance(1);
        check(octoberR3.ConfigRevision==3&&octoberR3.Overrides?.BaseRecipeHash==AggregateCanonical.Hash(r3)
            &&octoberR3.Overrides.TimeRules.Single().Mode=="CUMULATIVE_FROM"&&(await Fields(1)).GetProperty("metric").GetRawText()=="30",
            "P05 selected October migrates to r3 retaining explicitly chosen cumulative override");
        check((await Instance(0)).ConfigRevision==2&&(await Instance(2)).ConfigRevision==2
            &&(await Target(0)).PayloadHash==septemberR2Report.PayloadHash&&(await Target(2)).PayloadHash==novemberR2Report.PayloadHash,
            "P05 r3 leaves unselected September/November r2 pins and payloads intact");
        var stale=await Call("configs/"+configId+"/revisions",new AggregateConfigRevisionCommandDto(run+"-stale-common-r2",septemberContext,impact,impactPreview.GetProperty("token").GetString()!),HttpStatusCode.Conflict);
        check(stale.GetProperty("code").GetString()=="AGG_REVISION_CONFLICT","P05 old common revision request cannot republish after head changes");

        // Returning to the pinned base is itself a reviewed write, not an implicit reset.
        octoberContext=await Context(1);var beforeReset=await Target(1);
        var reset=new AggregateMappingChangeDto(octoberR3.Revision,null,selection,[],true);
        var resetPreview=await Call("instances/"+instanceIds[1]+"/mapping/preview",new AggregateMappingPreviewCommandDto(octoberContext,reset));
        check(Result(resetPreview.GetProperty("preview").GetProperty("preview"),"metric").GetProperty("value").GetString()=="20"
            &&(await Target(1)).PayloadHash==beforeReset.PayloadHash,"P05 reset previews pinned r3 October20 without writing");
        await Call("instances/"+instanceIds[1]+"/apply",new AggregateMappingApplyCommandDto(run+"-october-reset-r3",octoberContext,reset,resetPreview.GetProperty("token").GetString()!));
        check((await Instance(1)).ConfigRevision==3&&(await Instance(1)).Overrides==null&&(await Fields(1)).GetProperty("metric").GetRawText()=="20",
            "P05 confirmed reset clears only October override and reads back pinned r3 result20");
        check((await Instance(0)).ConfigRevision==2&&(await Instance(2)).ConfigRevision==2,
            "P05 reset of one period does not migrate other period instances");
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-periodic-revisions-"+run+".json",
            JsonSerializer.Serialize(new{run,state="PASS",workId=f.WorkId,configId,headRevision=3,
                targetReportIds=targets.Select(r=>r.Id),sourceReportIds=sources.Select(r=>r.Id),instanceIds,
                finalPinnedConfigRevisions=new[]{2,3,2},finalNumericResults=new[]{10,20,60},artifactJobs,
                scope="APPROVED_SEEDED_SOURCES_REAL_HTTP_JOB_NATIVE_WRITER_NO_BROWSER_UAT"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
