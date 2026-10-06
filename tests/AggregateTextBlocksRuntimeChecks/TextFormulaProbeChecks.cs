using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class TextFormulaProbeChecks
{
    internal static async Task Run(MongoDbContext db,HttpClient http,string run,string targetId,string sourceId,Action<bool,string> check,CancellationToken ct)
    {
        async Task<JsonElement> Post(string path,object body){using var response=await http.PostAsJsonAsync("aggregate-v2/"+path,body,AggregateCanonical.Json,ct);var raw=await response.Content.ReadAsStringAsync(ct);if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new Exception(path+" "+raw);return JsonSerializer.Deserialize<JsonElement>(raw);}
        var target=await db.WorkAssignmentReports.Find(r=>r.Id==targetId).SingleAsync(ct);
        var source=await db.WorkAssignmentReports.Find(r=>r.Id==sourceId).SingleAsync(ct);
        var sources=await db.WorkAssignmentReports.Find(r=>r.WorkId==source.WorkId&&r.WorkAssignmentId==source.WorkAssignmentId&&r.DynamicFormTemplateId==source.DynamicFormTemplateId&&r.IsCurrent&&r.IsActive&&!r.IsDeleted&&r.Status==WorkAssignmentReportStatus.Approved).SortBy(r=>r.PeriodKey).ToListAsync(ct);
        if(sources.Count!=3)throw new Exception("Expected three isolated monthly fixture reports");
        var boot=await Post("editor/bootstrap",new{reportId=targetId});
        check(boot.GetProperty("capabilities").GetProperty("formulaProbe").GetProperty("supported").GetBoolean(),"formula trial capability requires real authority");
        var context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var current=await Post("contexts/read",new{context});
        var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",
            Nodes=[new(){Id="s",Kind="SOURCE",Form=new(source.DynamicFormTemplateId!,source.DynamicFormFamilyId!,source.DynamicFormVersionNo!.Value,source.DynamicFormSchemaHash!),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("score","NUMBER","SET","score","w")]},
                new(){Id="c",Kind="CALCULATION",Inputs=[new("A","NUMBER","SET")],Outputs=[new("result","NUMBER","SINGLE")],Expressions=[new("result",new(){Kind="CALL",Name="SUM",Arguments=[new(){Kind="INPUT",Ref="A"}]})]},
                new(){Id="t",Kind="TARGET",Form=boot.GetProperty("target").GetProperty("pin").Deserialize<AggregateFormPinDto>(AggregateCanonical.Json)!,Inputs=[],Outputs=[]}],
            Edges=[new("e",new("s","score"),new("c","A"))],
            TimeRules=[new(){Id="w",Mode="EXPLICIT_RANGE",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED",StartDate="2026-09-01",EndDate="2026-11-30"}]};
        var selection=new AggregateInstanceSelectionDto([new("s","EXPLICIT_REPORTS",sources.Select(s=>s.Id).ToList(),[])]);
        var request=new AggregatePreviewRequestDto(context,current.GetProperty("instanceId").GetString()!,current.GetProperty("config").GetProperty("id").GetString()!,current.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!,recipe,selection);
        var clock=Stopwatch.StartNew();var job=await Post("preview-jobs/start",new AggregatePreviewJobRequest("FORMULA",context,request,null,null,FormulaNodeId:"c"));var ackMs=clock.ElapsedMilliseconds;
        var id=job.GetProperty("id").GetString()!;
        while(job.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){
            if(clock.Elapsed>TimeSpan.FromSeconds(40))throw new Exception("Formula trial timed out");
            await Task.Delay(150,ct);job=await Post("preview-jobs/"+id+"/read",new{});
        }
        check(job.GetProperty("state").GetString()=="COMPLETED","formula trial executes via real Hangfire: "+job.GetProperty("errorCode"));
        var result=job.GetProperty("result");var probe=result.GetProperty("formulaProbe");
        check(probe.GetProperty("inputs")[0].GetProperty("count").GetInt32()==3&&probe.GetProperty("outputs")[0].GetProperty("items")[0].GetProperty("value").GetString()=="60","formula trial native payload 10+20+30=60");
        var after=await db.WorkAssignmentReports.Find(r=>r.Id==targetId).SingleAsync(ct);
        check(after.PayloadHash==target.PayloadHash&&after.PayloadRevision==target.PayloadRevision&&after.LifecycleRevision==target.LifecycleRevision&&!result.TryGetProperty("token",out _),"formula trial never mutates report or issues Apply token");
        var reopened=await Post("preview-jobs/current",new{context});
        check(reopened.GetProperty("current").ValueKind==JsonValueKind.Null||reopened.GetProperty("current").GetProperty("kind").GetString()!="FORMULA","formula trials cannot replace editor mapping preview on reopen");
        var invalid=request with{Selection=new([new("s","FORM_SELECTOR",[],[])])};
        using var rejected=await http.PostAsJsonAsync("aggregate-v2/preview-jobs/start",new AggregatePreviewJobRequest("FORMULA",context,invalid,null,null,FormulaNodeId:"c"),AggregateCanonical.Json,ct);
        check(!rejected.IsSuccessStatusCode&&(await rejected.Content.ReadAsStringAsync(ct)).Contains("AGG_FORMULA_PROBE_SOURCE_LIMIT"),"formula trial rejects unbounded report selection");
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"formula-trial-"+run+".json"),JsonSerializer.Serialize(new{run,targetId,sourceIds=sources.Select(s=>s.Id),jobId=id,ackMs,totalMs=clock.ElapsedMilliseconds,bytes=result.GetRawText().Length,expected="60",scope="THREE_REAL_MONTHLY_REPORTS_ONE_UNIT_NORMAL_HTTP_HANGFIRE_NOT_BROWSER_UAT"},new JsonSerializerOptions{WriteIndented=true}),ct);
        if(Environment.GetEnvironmentVariable("AGG_FORMULA_BROWSER")=="1"){
            // Ephemeral credential outside the served workspace, removed after the private browser check.
            var bridge=Path.Combine(Path.GetTempPath(),"tdtd-formula-browser.json");
            try{
                await File.WriteAllTextAsync(bridge,JsonSerializer.Serialize(new{api=http.BaseAddress!.ToString(),token=http.DefaultRequestHeaders.Authorization!.Parameter,recipe,context,targetId},AggregateCanonical.Json),ct);
                Console.WriteLine("FORMULA_BROWSER_READY own fixture, read-only trial UI, five-minute maximum");
                var hold=Stopwatch.StartNew();while(File.Exists(bridge)&&hold.Elapsed<TimeSpan.FromMinutes(5)){await Task.Delay(500,ct);}
            }finally{if(File.Exists(bridge))File.Delete(bridge);}
        }
    }
}
