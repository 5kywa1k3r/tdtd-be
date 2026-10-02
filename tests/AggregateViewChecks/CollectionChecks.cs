using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class CollectionChecks
{
    internal static async Task<RuntimeFixture> Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, IConfiguration config, string run, CancellationToken ct)
    {
        RuntimeFixture last = null!;
        foreach (var mode in new[] { "LIST", "CONTENT", "MATRIX" })
        {
            var table = new DynamicFormNativeTableDto { Id="items", SectionId="main", Name="Dữ liệu giả — "+mode, Order=0,
                Layout=mode=="MATRIX"?"matrix":"vertical", Fields=[new(){Id="unit",Name="Đơn vị báo cáo",Order=0},new(){Id="body",Name="Nội dung",Order=1}],
                Rows=mode=="MATRIX"?[new(){Id="fixed",Name="Hàng cố định"}]:[], StatisticTargets=[],
                Presentation=mode=="LIST"?new(){Kind="LIST",ItemLabel="Nội dung",AddLabel="Thêm nội dung",SummaryFieldIds=["body"]}:null,
                ItemConstraints=mode=="LIST"?new(){MinItems=0,MaxItems=200}:null,
                TypeConfig=new(){Version=1,Sequence=2,Rules=[new(){Order=1,Target=new(){Scope="column",FieldId="unit"},Spec=new(){Type="plainText",Required=false}},
                    new(){Order=2,Target=new(){Scope="column",FieldId="body"},Spec=new(){Type="plainText",Required=false}}]} };
            var f=await RuntimeFixture.Seed(db,run+"-"+mode.ToLowerInvariant(),ct,configureForm:(form,target)=>{
                form.FieldsJson="[]";
                if(mode=="CONTENT"&&!target) form.FieldsJson="[{\"id\":\"n\",\"key\":\"n\",\"name\":\"Nội dung\",\"label\":\"Nội dung\",\"sectionId\":\"main\",\"type\":\"longText\",\"required\":false}]";
                else {form.NativeTablesVersion=mode=="LIST"?2:1;form.TablesJson=JsonSerializer.Serialize(new[]{table},AggregateCanonical.Json);}
            });
            last=f;
            var text="DỮ LIỆU GIẢ — "+new string('x',8000);
            var record=mode=="MATRIX"?new Dictionary<string,object>{{"rowId","fixed"}}:new Dictionary<string,object>{{"recordId",Guid.NewGuid().ToString()}};
            record["cells"]=new {unit=new{type="plainText",state="value",value="Đơn vị giả"},body=new{type="plainText",state="value",value=text}};
            var values=mode=="CONTENT"?null:JsonSerializer.Serialize(new {nativeTables=new{version=1,schemaHash=f.SourceForm.PublishedSchemaHash,
                tables=new[]{new Dictionary<string,object>{{"tableId","items"},{mode=="MATRIX"?"rows":"records",new[]{record}}}}}},AggregateCanonical.Json);
            var payload=new WorkReportPayloadService(db);var write=await payload.SaveReportPayloadAsync(f.Source,"[]",mode=="CONTENT"?JsonSerializer.Serialize(new{n=text}):"{}",values,null,f.Actor,DateTime.UtcNow,ct);
            f.Source.PayloadRevision=write.PayloadRevision;f.Source.PayloadHash=write.PayloadHash;f.Source.PayloadSizeBytes=write.PayloadSizeBytes;f.Source.PayloadStatus=write.PayloadStatus;
            await db.WorkAssignmentReports.ReplaceOneAsync(x=>x.Id==f.Source.Id,f.Source,cancellationToken:ct);
            var store=new AggregateMongoStore(db,runner,payload,payload);
            var period=await db.WorkReportPeriods.Find(x=>x.Id==f.Source.WorkReportPeriodId).SingleAsync(ct);
            await store.ExecuteAsync(async(tx,t)=>{foreach(var (kind,id) in new[]{("REPORT",f.Source.Id),("SLOT",period.WorkTemplateAssigneeId+":ONCE")})
                await tx.PutAsync(AggregateCollections.Declarations,kind+":"+id,0,new AggregateDeclarationState(kind,id,f.WorkId,f.Source.WorkAssignmentId,new("2026-09-01","2026-09-30","USER_DECLARED",run,1)),f.WorkId,id,[],t);return true;},ct);
            var http=new DefaultHttpContext { User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,f.Actor),new Claim("sid",run)],"fixture")) };
            var controller=new AggregateViewsController(db,runner,payload,payload,config){ControllerContext=new(){HttpContext=http}};
            JsonElement Ok(IActionResult action)=>action is OkObjectResult ok?JsonSerializer.SerializeToElement(ok.Value,AggregateCanonical.Json):throw new Exception(JsonSerializer.Serialize(action));
            var owner=new AggregateViewOwnerRequest(f.WorkId,"ASSIGNMENT",f.Binding.WorkAssignmentId);
            var bind=new AggregateViewBindRequest(owner,f.TargetForm.Id,0);
            var bindToken=Ok(await controller.Preview(bind,ct)).GetProperty("confirmationToken").GetString();
            var context=Ok(await controller.Bind(bind with{ConfirmationToken=bindToken},ct)).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            var reader=new AggregateMongoCommandReader(db,payload,true);context=(await reader.AuthorizeAsync(context,f.Actor,run,ct)).Read.Context;
            AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
            var type=mode=="LIST"?"LIST":"TABLE";
            var expression=mode=="LIST"?new AggregateExpressionDto{Kind="LIST_PIPELINE",Ref="in",ListPipeline=new(){Version=1,Scope="ALL_SOURCES",Take="ALL",Sort=[],Project=[new("unit","unit"),new("body","body")],Operation="COLLECT"}}
                :new AggregateExpressionDto{Kind="CALL",Name=mode=="CONTENT"?"REPORT_TEXT_TABLE":"ONLY",Arguments=[new(){Kind="INPUT",Ref="in"}],Options=mode=="CONTENT"?new(){Order="UNIT_THEN_PERIOD"}:null};
            var recipe=new AggregateRecipeDto{SchemaVersion=mode=="LIST"?2:1,SemanticProfile=mode=="LIST"?"REPORT_MAPPING_LIST_V1":"REPORT_MAPPING_V1",
                Nodes=[new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("out",mode=="CONTENT"?"TEXT":type,"SET",mode=="CONTENT"?"n":"items","w")]},
                    new(){Id="c",Kind="CALCULATION",Inputs=[new("in",mode=="CONTENT"?"TEXT":type,"SET")],Outputs=[new("out",type,"SINGLE")],Expressions=[new("out",expression)]},
                    new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("in",type,"SINGLE","items")],Outputs=[]}],
                Edges=[new("e1",new("s","out"),new("c","in")),new("e2",new("c","out"),new("t","in"))],TimeRules=[new(){Id="w",Mode="EXPLICIT_RANGE",StartDate="2026-09-01",EndDate="2026-09-30",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED"}]};
            AggregateCommandContext Command(string op)=>new(Guid.NewGuid().ToString("N"),op,context.View!.ViewId,f.Actor,run,DateTimeOffset.UtcNow);
            var commands=new AggregateCommandService(store,reader,new(Convert.FromBase64String(config["AggregateMapping:ConfirmationKeyBase64"]!)));
            var cfg=await commands.CreateConfigAsync(Command("CREATE_CONFIG"),context,new(Guid.NewGuid().ToString(),context.BindingId,Pin(f.TargetForm),recipe),ct);
            var instance=await commands.CreateInstanceAsync(Command("CREATE_INSTANCE"),context,cfg.Id,ct);
            var change=new AggregateMappingChange(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
            var preview=await commands.PreviewChangeAsync(Command("APPLY"),context,instance.Id,change,ct);
            await commands.ApplyAsync(Command("APPLY"),context,instance.Id,change,preview.Token,ct);
            var read=Ok(await controller.Read(owner,ct));
            var result=read.GetProperty("result").GetProperty("lastResults")[0].GetProperty("value");
            if(mode=="LIST")
            {
                var pageController=new AggregateListController(db,payload,config){ControllerContext=new(){HttpContext=http}};
                var page=Ok(await pageController.Page(new("",null,result.GetProperty("id").GetString()!,result.GetProperty("hash").GetString()!,ReadId:read.GetProperty("listReadId").GetString(),ViewId:context.View!.ViewId,IncludeLineage:true),ct));
                if(page.GetProperty("total").GetInt32()!=1||page.GetProperty("items")[0].GetProperty("cells").GetProperty("body").GetProperty("value").GetString()!.Length>500)throw new Exception("unbounded list page");
            }
            else if(mode=="CONTENT")
            {
                var content=new AggregateContentController(db,payload,null!,config){ControllerContext=new(){HttpContext=http}};
                var page=Ok(await content.Page(new("",null,null,null,result,ViewId:context.View!.ViewId,ListReadId:read.GetProperty("listReadId").GetString()),ct));
                if(page.GetProperty("rows")[0].GetProperty("sample").GetString()!.Length>240)throw new Exception("unbounded content page");
                var intents=await store.ExecuteAsync((tx,t)=>tx.QueryAsync<AggregateContentSnapshotIntent>(AggregateCollections.ContentSnapshots,new(f.WorkId,context.View.ViewId),t),ct);
                if(intents.Count!=1||intents[0].Value.ViewId!=context.View.ViewId)throw new Exception("missing view snapshot intent");
            }
            else if(result.GetProperty("rows").GetArrayLength()!=1||result.GetProperty("columns")[0].GetString()!="unit")throw new Exception("matrix shape lost");
            Console.WriteLine("PASS standalone "+mode+" preview / canonical validation / Apply / readback / bounded paging or matrix shape");
        }
        return last;
    }
}
