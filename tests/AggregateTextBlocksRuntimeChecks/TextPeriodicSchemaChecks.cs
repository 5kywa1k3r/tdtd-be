using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

// Normal authenticated Form/version/view APIs over the lifecycle harness's own
// fixture. Never repin an existing report or edit a published schema in place.
internal static class TextPeriodicSchemaChecks
{
    internal static async Task Run(MongoDbContext db, RuntimeFixture f, string run, string configId,
        IReadOnlyList<string> instanceIds, IReadOnlyList<WorkAssignmentReport> targets,
        IReadOnlyList<WorkAssignmentReport> sources, HttpClient http, Action<bool,string> check, CancellationToken ct)
    {
        var output="../outputs/aggregate-operators-20261005/";var local=0;
        void Check(bool value,string label){check(value,"P05 schema "+label);local++;}
        async Task<(HttpStatusCode Status,JsonElement Body)> Send(string path,object body,HttpMethod? method=null)
        {
            using var message=new HttpRequestMessage(method??HttpMethod.Post,path){Content=JsonContent.Create(body,options:AggregateCanonical.Json)};
            using var response=await http.SendAsync(message,ct);var raw=await response.Content.ReadAsStringAsync(ct);
            var result=string.IsNullOrEmpty(raw)?default:JsonSerializer.Deserialize<JsonElement>(raw);
            if((int)response.StatusCode>=500)throw new InvalidOperationException(path+" returned "+(int)response.StatusCode+": "+raw);
            return(response.StatusCode,result);
        }
        async Task<JsonElement> Ok(string path,object body,HttpMethod? method=null)
        {
            var result=await Send(path,body,method);
            if(result.Status is not(HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.Created))
                throw new InvalidOperationException(path+" returned "+(int)result.Status+": "+result.Body);
            return result.Body;
        }
        Task<DynamicFormTemplate> Form(string id)=>db.DynamicFormTemplates.Find(t=>t.Id==id&&!t.IsDeleted).SingleAsync(ct);
        AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
        var reportIds=targets.Concat(sources).Select(r=>r.Id).ToArray();
        async Task<string> Reports()=>AggregateCanonical.Hash((await db.WorkAssignmentReports.Find(r=>reportIds.Contains(r.Id)).SortBy(r=>r.Id).ToListAsync(ct))
            .Select(r=>new{r.Id,r.DynamicFormTemplateId,r.DynamicFormFamilyId,r.DynamicFormVersionNo,r.DynamicFormSchemaHash,r.PayloadRevision,r.PayloadHash,r.LifecycleRevision,r.Status}));
        async Task<string> Frozen()=>AggregateCanonical.Hash((await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen)
            .Find(new BsonDocument("workId",f.WorkId)).Sort(new BsonDocument("_id",1)).ToListAsync(ct)).Select(r=>r.ToJson()));
        async Task<AggregateInstanceState> Instance(int i)=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
            .Find(new BsonDocument("_id",instanceIds[i])).SingleAsync(ct)).Value;
        var beforeReports=await Reports();var beforeFrozen=await Frozen();var beforeInstances=new[]{await Instance(0),await Instance(1),await Instance(2)};
        await File.WriteAllTextAsync(output+"p05-periodic-schema-backup-"+run+".json",JsonSerializer.Serialize(new{run,workId=f.WorkId,
            reportsDigest=beforeReports,frozenDigest=beforeFrozen,instances=beforeInstances,sourceFormPin=Pin(f.SourceForm),targetFormPin=Pin(f.TargetForm)},new JsonSerializerOptions{WriteIndented=true}),ct);
        async Task<DynamicFormTemplate> Next(DynamicFormTemplate previous,string suffix)
        {
            var current=await Form(previous.Id);
            var draft=await Ok("dynamic-forms/"+previous.Id+"/versions",new{expectedRevision=current.Revision,name="[DỮ LIỆU GIẢ P05] "+suffix});
            var schema=JsonNode.Parse(draft.GetProperty("schema").GetRawText())!.AsObject();
            schema["fields"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{id="version-note-"+suffix,key="versionNote"+suffix,name="Ghi chú "+suffix,sectionId="main",type="longText",required=false}));
            var edited=await Ok("dynamic-forms/"+draft.GetProperty("id").GetString(),new{
                code=draft.GetProperty("code").GetString(),name=draft.GetProperty("name").GetString(),description="Fixture P05 riêng, không dữ liệu nghiệm thu",
                tagCodes=Array.Empty<string>(),schemaVersion=draft.GetProperty("schemaVersion").GetInt32(),schema,isActive=true,
                expectedRevision=draft.GetProperty("revision").GetInt32()},HttpMethod.Put);
            await Ok("dynamic-forms/"+draft.GetProperty("id").GetString()+"/publish",new{expectedRevision=edited.GetProperty("revision").GetInt32()});
            var next=await Form(draft.GetProperty("id").GetString()!);var old=await Form(previous.Id);
            Check(next.IsPublished&&next.FamilyId==previous.FamilyId&&next.VersionNo==previous.VersionNo+1&&next.PreviousVersionId==previous.Id
                &&next.PublishedSchemaHash!=previous.PublishedSchemaHash,"normal create/edit/publish produces a new pinned schema "+suffix);
            Check(old.FieldsJson==previous.FieldsJson&&old.TablesJson==previous.TablesJson&&old.PublishedSchemaSnapshotJson==previous.PublishedSchemaSnapshotJson
                &&old.PublishedSchemaHash==previous.PublishedSchemaHash,"published predecessor structure/hash unchanged "+suffix);
            return next;
        }
        var sourceV2=await Next(f.SourceForm,"source2");var targetV2=await Next(f.TargetForm,"target2");
        Check(await Reports()==beforeReports&&await Frozen()==beforeFrozen,"publishing new source/target Forms leaves existing report pins/payload/lifecycle/history unchanged");
        var context=(await Ok("aggregate-v2/editor/bootstrap",new{reportId=targets[0].Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var schemaRead=await Ok("aggregate-v2/editor/source-schema",new AggregateEditorSchemaQueryDto(context,Pin(f.SourceForm)));
        Check(schemaRead.GetRawText().Contains(f.SourceForm.PublishedSchemaHash!,StringComparison.Ordinal)&&!schemaRead.GetRawText().Contains(sourceV2.PublishedSchemaHash!,StringComparison.Ordinal),
            "pinned old source schema remains readable after newer family version publishes");
        var r4=(await Ok("aggregate-v2/configs/"+configId+"/read",new AggregateConfigReadCommandDto(context,4))).GetProperty("version").GetProperty("recipe").Deserialize<AggregateRecipeDto>(AggregateCanonical.Json)!;
        var changedTarget=r4 with{Nodes=r4.Nodes.Select(n=>n.Kind=="TARGET"?n with{Form=Pin(targetV2)}:n).ToList()};
        var impact=new AggregateConfigImpactRequestDto(4,changedTarget,[new(instanceIds[0],(await Instance(0)).Revision)],[]);
        var unsupported=await Send("aggregate-v2/configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(context,impact));
        Check((int)unsupported.Status>=400&&unsupported.Body.GetRawText().Contains("AGG_SCHEMA_INCOMPATIBLE",StringComparison.Ordinal),
            "report config target schema migration is explicitly rejected by current contract");
        Check(await Reports()==beforeReports&&await Frozen()==beforeFrozen&&AggregateCanonical.Hash(beforeInstances)==AggregateCanonical.Hash(new[]{await Instance(0),await Instance(1),await Instance(2)}),
            "rejected report target migration preserves all periodic pins/overrides/payload/history");

        // These are direct children of the receiving assignment. A Work-level
        // view sees the root assignment instead, not these grandchildren.
        var owner=new AggregateViewOwnerRequest(f.WorkId,"ASSIGNMENT",f.Binding.WorkAssignmentId);
        var viewId=AggregateCanonical.Key("VIEW","ASSIGNMENT",f.Binding.WorkAssignmentId);
        async Task<BsonDocument?> ViewRow()=>await db.Db.GetCollection<BsonDocument>(AggregateCollections.Views).Find(new BsonDocument("_id",viewId)).FirstOrDefaultAsync(ct);
        var bindings=new List<string>();var viewInstances=new List<string>();var forms=new List<string>{f.TargetForm.Id,targetV2.Id};
        async Task<AggregatePeriodContextDto> Bind(DynamicFormTemplate form)
        {
            var before=await ViewRow();var expected=before?["version"].ToInt64()??0;
            var request=new AggregateViewBindRequest(owner,form.Id,expected);
            var preview=await Ok("aggregate-v2/views/binding/preview",request);
            Check((await ViewRow())?.ToJson()==before?.ToJson(),"binding preview does not write view for Form version "+form.VersionNo);
            if(form.VersionNo==4)Check(preview.GetProperty("discardedVersions").GetArrayLength()==1&&preview.GetProperty("discardedVersions")[0].GetProperty("formId").GetString()==f.TargetForm.Id
                &&preview.GetProperty("warning").GetString()!.Contains("ba bản",StringComparison.Ordinal),"fourth binding warns exactly which old generation is discarded");
            var saved=await Ok("aggregate-v2/views/binding/apply",request with{ConfirmationToken=preview.GetProperty("confirmationToken").GetString()!});
            var read=await Ok("aggregate-v2/views/read",owner);var result=read.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            bindings.Add(result.BindingId);Check(result.View!=null&&result.ReportId==null&&read.GetProperty("target").GetRawText().Contains(form.PublishedSchemaHash!,StringComparison.Ordinal),
                "view binding reads exact new Form pin without changing a report "+form.VersionNo);
            return result;
        }
        var viewContext=await Bind(f.TargetForm);
        var declaration=new AggregateDataWindowDeclarationDto("2026-09-01","2026-09-30","USER_DECLARED",run,1);
        var declarationPreview=await Ok("aggregate-v2/data-window/preview",new AggregateDeclarationPreviewCommandDto(viewContext,declaration));
        await Ok("aggregate-v2/data-window/apply",new AggregateDeclarationSaveCommandDto(run+"-view-window",viewContext,declaration,declarationPreview.GetString()!));
        viewContext=(await Ok("aggregate-v2/views/read",owner)).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        async Task<(string Instance,AggregateMappingChangeDto Change,string Token)> Configure(StableViewInput input)
        {
            var recipe=r4 with{Nodes=r4.Nodes.Select(n=>n.Kind=="TARGET"?n with{Form=Pin(input.Form)}:n).ToList(),
                TimeRules=[r4.TimeRules.Single() with{Mode="EXPLICIT_RANGE",StartDate="2026-09-01",EndDate="2026-09-30"}]};
            var config=await Ok("aggregate-v2/configs",new AggregateConfigCreateCommandDto(input.Context,new(run+"-view-config-"+input.Form.VersionNo,input.Context.BindingId,Pin(input.Form),recipe)));
            var instance=await Ok("aggregate-v2/instances",new AggregateInstanceCreateCommandDto(run+"-view-instance-"+input.Form.VersionNo,input.Context,config.GetProperty("id").GetString()!));
            var id=instance.GetProperty("id").GetString()!;viewInstances.Add(id);
            var change=new AggregateMappingChangeDto(1,null,new([new("s","EXPLICIT_REPORTS",[sources[0].Id],[])]),[],false);
            var prepared=await Ok("aggregate-v2/instances/"+id+"/mapping/preview",new AggregateMappingPreviewCommandDto(input.Context,change));
            var results=prepared.GetProperty("preview").GetProperty("preview").GetProperty("results");
            Check(results.EnumerateArray().Single(r=>r.GetProperty("portId").GetString()=="metric").GetProperty("value").GetString()=="10",
                "new view generation explicitly previews pinned September source result10 "+input.Form.VersionNo);
            var token=prepared.GetProperty("token").GetString()!;
            await Ok("aggregate-v2/instances/"+id+"/apply",new AggregateMappingApplyCommandDto(run+"-view-apply-"+input.Form.VersionNo,input.Context,change,token));
            var read=await Ok("aggregate-v2/views/read",owner);
            Check(read.GetProperty("view").GetProperty("current").GetProperty("instanceId").GetString()==id&&read.GetProperty("result").GetProperty("lastResults").EnumerateArray()
                .Single(r=>r.GetProperty("portId").GetString()=="metric").GetProperty("value").GetString()=="10","view Apply/readback uses its generation "+input.Form.VersionNo);
            return(id,change,token);
        }
        var original=await Configure(new(viewContext,f.TargetForm));
        var oldContext=viewContext;viewContext=await Bind(targetV2);
        var oldHistory=await Ok("aggregate-v2/views/read",owner with{HistoryBindingId=bindings[0]});
        Check(!oldHistory.GetProperty("canEdit").GetBoolean()&&oldHistory.GetProperty("result").GetProperty("lastResults").EnumerateArray()
            .Single(r=>r.GetProperty("portId").GetString()=="metric").GetProperty("value").GetString()=="10","previous view generation remains readable with old pin/result and read-only");
        var stale=await Send("aggregate-v2/instances/"+original.Instance+"/apply",new AggregateMappingApplyCommandDto(run+"-stale-view",oldContext,original.Change,original.Token));
        Check((int)stale.Status>=400,"old view confirmation cannot write after Form rebind");
        await Configure(new(viewContext,targetV2));
        var targetV3=await Next(targetV2,"target3");forms.Add(targetV3.Id);viewContext=await Bind(targetV3);await Configure(new(viewContext,targetV3));
        var targetV4=await Next(targetV3,"target4");forms.Add(targetV4.Id);viewContext=await Bind(targetV4);await Configure(new(viewContext,targetV4));
        var final=await Ok("aggregate-v2/views/read",owner);var history=final.GetProperty("view").GetProperty("history");
        Check(history.GetArrayLength()==2&&history.EnumerateArray().Select(h=>h.GetProperty("form").GetProperty("versionNo").GetInt32()).SequenceEqual(new[]{2,3})
            &&final.GetProperty("view").GetProperty("current").GetProperty("form").GetProperty("versionNo").GetInt32()==4,"retention keeps exactly current plus two previous generations");
        var evicted=await Send("aggregate-v2/views/read",owner with{HistoryBindingId=bindings[0]});
        Check((int)evicted.Status>=400&&!await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",original.Instance)).AnyAsync(ct),
            "evicted view instance cannot be read or fall back to another generation");
        Check(await db.DynamicFormTemplates.Find(t=>forms.Contains(t.Id)&&!t.IsDeleted&&t.IsPublished).CountDocumentsAsync(ct)==4,
            "view retention preserves all published Form versions");
        Check(await Reports()==beforeReports&&await Frozen()==beforeFrozen,"Form/view version operations preserve all periodic report values/pins/frozen history");
        await FixtureProvenanceChecks.Migration(db,"P05 after schema/version/view API",ct);
        await File.WriteAllTextAsync(output+"p05-periodic-schema-"+run+".json",JsonSerializer.Serialize(new{run,state="PASS",checks=local,workId=f.WorkId,
            sourceFormV1=f.SourceForm.Id,sourceFormV2=sourceV2.Id,targetFormVersions=forms,bindings,viewInstances,viewId,
            reportTargetMigration="CURRENT_CONTRACT_REJECTS_AGG_SCHEMA_INCOMPATIBLE_NOT_IMPLEMENTED",scope="NORMAL_AUTHENTICATED_FORM_AND_AGGREGATE_VIEW_HTTP_NO_BROWSER"},new JsonSerializerOptions{WriteIndented=true}),ct);
    }
    private sealed record StableViewInput(AggregatePeriodContextDto Context,DynamicFormTemplate Form);
}
