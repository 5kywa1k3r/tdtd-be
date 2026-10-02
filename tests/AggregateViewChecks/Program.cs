using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

HandoverScopeChecks.Run();
if(args.Contains("--prepare-browser"))
{
    var fixture=JsonDocument.Parse(await File.ReadAllTextAsync("outputs/aggregate-view-browser-fixture.json")).RootElement;
    var database=fixture.GetProperty("database").GetString()!;
    if(!database.StartsWith("aggregate_views_",StringComparison.Ordinal)) throw new Exception("private fixture database required");
    var fixtureDb=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database=database}));
    var username=fixture.GetProperty("username").GetString();
    var user=await fixtureDb.Users.Find(x=>x.Username==username).SingleAsync();
    var work=fixture.GetProperty("workId").GetString()!;
    if(await fixtureDb.Units.CountDocumentsAsync(x=>x.Id==user.UnitId)==0)
    {
        var root=ObjectId.GenerateNewId().ToString();
        await fixtureDb.Units.InsertManyAsync(new[]{new Unit{Id=root,Code="100",FullName="DỮ LIỆU GIẢ — Cấp cha",CreatedByUserId=user.Id},
            new Unit{Id=user.UnitId!,Code="100001",ParentUnitId=root,FullName="DỮ LIỆU GIẢ — Đơn vị của tôi",CreatedByUserId=user.Id},
            new Unit{Id=ObjectId.GenerateNewId().ToString(),Code="100001001",ParentUnitId=user.UnitId,FullName="DỮ LIỆU GIẢ — Đơn vị con",CreatedByUserId=user.Id},
            new Unit{Id=ObjectId.GenerateNewId().ToString(),Code="100002",ParentUnitId=root,FullName="DỮ LIỆU GIẢ — Đơn vị ngang cấp",CreatedByUserId=user.Id}});
    }
    var projection=new tdtd_be.Services.Common.DocRoleReadModelProjectionService(fixtureDb);
    await projection.RebuildWorkAsync(work,user.Id,CancellationToken.None);
    await projection.RebuildWorkAssignmentsAsync(work,user.Id,CancellationToken.None);
    Console.WriteLine("Prepared private browser fixture projections and unit tree; acceptance database untouched");
    return;
}
var run = "aggregate_views_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = run }));
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)); var ct = timeout.Token;
Console.WriteLine("PRIVATE DATABASE " + run + "; no acceptance data or existing host changed");
var payloads = new WorkReportPayloadService(db);
var runner = new DynamicFlowDefinitionTransactionRunner(db, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
var store = new AggregateMongoStore(db, runner, payloads, payloads);
var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["AggregateMapping:V2Enabled"]="true", ["AggregateMapping:ConfirmationKeyBase64"] = Convert.ToBase64String(secret)
}).Build();
var f = await RuntimeFixture.Seed(db, run, ct);
await db.DocRoles.InsertOneAsync(new DocRole { Id = ObjectId.GenerateNewId().ToString(), DocId=f.WorkId, DocType=DocType.WORK, UserId=f.Actor, Role=DocRoleType.OWNER, CreatedByUserId=f.Actor }, cancellationToken:ct);
await FixtureProvenanceChecks.Migration(db, "after final fixture pins", ct);
var checks=0;
void Check(bool yes,string name) { if(!yes)throw new Exception(name);checks++;Console.WriteLine("PASS "+name); }
AggregateViewsController Controller(string? actor=null) => new(db,runner,payloads,payloads,config) { ControllerContext=new() { HttpContext=new DefaultHttpContext { User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,actor??f.Actor),new Claim("sid",run)],"fixture")) } } };
JsonElement Ok(IActionResult response) { if(response is not OkObjectResult ok)throw new Exception(JsonSerializer.Serialize(response));return JsonSerializer.SerializeToElement(ok.Value,AggregateCanonical.Json); }
AggregateCommandContext Command(string operation) => new(Guid.NewGuid().ToString("N"),operation,"fixture",f.Actor,run,DateTimeOffset.UtcNow);
var reader = new AggregateMongoCommandReader(db,payloads,true);
var service = new AggregateCommandService(store,reader,new(secret));
var owner = new AggregateViewOwnerRequest(f.WorkId,"ASSIGNMENT",f.Binding.WorkAssignmentId);
async Task<AggregatePeriodContextDto> Bind(AggregateViewOwnerRequest selected,DynamicFormTemplate form) {
    var read=Ok(await Controller().Read(selected,ct));
    var request=new AggregateViewBindRequest(selected,form.Id,read.GetProperty("revision").GetInt64());
    var preview=Ok(await Controller().Preview(request,ct));
    var bound=Ok(await Controller().Bind(request with { ConfirmationToken=preview.GetProperty("confirmationToken").GetString() },ct));
    var boot=await new AggregateMongoPreviewReader(db,payloads,new AggregateMongoDataWindows(db),true).ReadContextAsync(bound.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!,f.Actor,ct);
    return boot.Context;
}
var context=await Bind(owner,f.TargetForm);
Check(context.ReportId==null && context.AssignmentId==f.Binding.WorkAssignmentId,"standalone assignment identity has no fabricated report");
var declaration=new AggregateDataWindowDeclarationDto("2026-09-01","2026-09-30","USER_DECLARED",run,1);
await store.ExecuteAsync(async(tx,t)=>{
    await tx.PutAsync(AggregateCollections.Declarations,"REPORT:"+f.Source.Id,0,new AggregateDeclarationState("REPORT",f.Source.Id,f.WorkId,f.Source.WorkAssignmentId,declaration),f.WorkId,f.Source.Id,[],t);
    var period=await db.WorkReportPeriods.Find(x=>x.Id==f.Source.WorkReportPeriodId).SingleAsync(t);
    await tx.PutAsync(AggregateCollections.Declarations,"SLOT:"+period.WorkTemplateAssigneeId+":ONCE",0,new AggregateDeclarationState("SLOT",period.WorkTemplateAssigneeId+":ONCE",f.WorkId,f.Source.WorkAssignmentId,declaration),f.WorkId,null,[],t);return true;
},ct);
var dateCommand=Command("DATA_WINDOW");
var dateToken=await service.PreviewDeclarationAsync(dateCommand,context,declaration,ct);
await service.SaveDeclarationAsync(dateCommand,context,declaration,dateToken,ct);
context=(await reader.AuthorizeAsync(context,f.Actor,run,ct)).Read.Context;
Check(context.DataEndDate=="2026-09-30","view data window stored separately from report/slot/deadline");
AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
var recipe=new AggregateRecipeDto { SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",
 Nodes=[new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("out","NUMBER","SET","n","w")]},
 new(){Id="c",Kind="CALCULATION",Inputs=[new("in","NUMBER","SET")],Outputs=[new("out","NUMBER","SINGLE")],Expressions=[new("out",new(){Kind="CALL",Name="SUM",Arguments=[new(){Kind="INPUT",Ref="in"}]})]},
 new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("in","NUMBER","SINGLE","total")],Outputs=[]}],
 Edges=[new("e1",new("s","out"),new("c","in")),new("e2",new("c","out"),new("t","in"))],
 TimeRules=[new(){Id="w",Mode="TARGET_DATA_WINDOW",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED"}] };
var configId=await service.CreateConfigAsync(Command("CREATE_CONFIG"),context,new(Guid.NewGuid().ToString(),context.BindingId,Pin(f.TargetForm),recipe),ct);
var instance=await service.CreateInstanceAsync(Command("CREATE_INSTANCE"),context,configId.Id,ct);
var change=new AggregateMappingChange(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
var jobs=new AggregatePreviewJobs(db,payloads,config);
var jobRequest=new AggregatePreviewJobRequest("MAPPING",context,null,instance.Id,new(1,null,change.Selection,[],false));
var jobId=await jobs.Start(jobRequest,f.Actor,run,ct);
await jobs.Run(jobId,runner,payloads,ct);
var job=JsonSerializer.SerializeToElement(await jobs.Read(jobId,f.Actor,run,ct),AggregateCanonical.Json);
Check(job.GetProperty("state").GetString()=="COMPLETED","real persisted preview job completes for standalone target");
var preview=await service.PreviewChangeAsync(Command("APPLY"),context,instance.Id,change,ct);
Check(preview.Preview.Preview.Results.Single().Value!.Value.GetString()=="30","assignment view aggregates its direct child Approved source");
var before=await db.WorkAssignmentReports.Find(x=>x.Id==f.Report.Id).SingleAsync(ct);
await service.ApplyAsync(Command("APPLY"),context,instance.Id,change,preview.Token,ct);
var saved=Ok(await Controller().Read(owner,ct));
Check(saved.GetProperty("result").GetProperty("lastResults")[0].GetProperty("value").GetString()=="30","readback returns committed shared view result");
var after=await db.WorkAssignmentReports.Find(x=>x.Id==f.Report.Id).SingleAsync(ct);
Check(before.PayloadRevision==after.PayloadRevision && before.PayloadHash==after.PayloadHash,"view Apply leaves assigned report payload unchanged");
Check(await Controller(f.Outsider).Read(owner,ct) is ObjectResult { StatusCode:409 },"unrelated actor cannot read view");
await db.WorkAssignments.UpdateOneAsync(x=>x.Id==f.Binding.WorkAssignmentId,Builders<WorkAssignment>.Update.Set(x=>x.LeaderWatcherUserIds,new List<string>{f.Outsider}),cancellationToken:ct);
var supervisor=Ok(await Controller(f.Outsider).Read(owner,ct));
Check(!supervisor.GetProperty("canEdit").GetBoolean() && supervisor.GetProperty("result").GetProperty("lastResults")[0].GetProperty("value").GetString()=="30",
    "ancestor read permission sees shared result without being config author/assignee");
await db.WorkAssignments.UpdateOneAsync(x=>x.Id==f.Binding.WorkAssignmentId,Builders<WorkAssignment>.Update.Set(x=>x.LeaderWatcherUserIds,new List<string>()),cancellationToken:ct);
Check(await Controller(f.Outsider).Read(owner,ct) is ObjectResult {StatusCode:409},"revoking existing branch permission stops further reads");
var staleChange=change with {ExpectedRevision=2};
var stale=await service.PreviewChangeAsync(Command("APPLY"),context,instance.Id,staleChange,ct);
await db.WorkAssignmentReports.UpdateOneAsync(x=>x.Id==f.Source.Id,Builders<WorkAssignmentReport>.Update.Inc(x=>x.LifecycleRevision,1),cancellationToken:ct);
try {await service.ApplyAsync(Command("APPLY"),context,instance.Id,staleChange,stale.Token,ct);throw new Exception("stale Apply accepted");}
catch(AggregatePreviewException e) when(e.Code.Contains("STALE")) { Check(true,"source revision changed after preview rejects token with no target write"); }
var currentInstance=(await store.ExecuteAsync((tx,t)=>tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances,instance.Id,t),ct))!;
await store.ExecuteAsync(async(tx,t)=>{await AggregateRefreshService.InvalidateAsync(tx,f.WorkId,["REPORT:"+f.Source.Id],"source-approved-change",t);return true;},ct);
Check(await new AggregateRefreshService(store,reader).RunAsync(instance.Id,currentInstance.Value.Generation,ct)=="COMPLETED","existing refresh worker also writes standalone view through its adapter");
async Task MoveViewAuthority(string actor)
{
    using var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct);session.StartTransaction();
    await AggregateHostIntegration.HandoverAsync(db,session,f.WorkId,f.Binding.WorkAssignmentId,f.Binding.Id,actor,Guid.NewGuid().ToString(),ct);
    var branch=await db.WorkAssignments.Find(session,x=>x.Id==f.Binding.WorkAssignmentId).SingleAsync(ct);
    var responsible=branch.Assignees.Single();responsible.UserId=actor;
    await db.WorkAssignments.UpdateOneAsync(session,x=>x.Id==branch.Id,Builders<WorkAssignment>.Update.Set(x=>x.Assignees,new List<UserRef>{responsible}),cancellationToken:ct);
    await db.WorkAssignments.UpdateOneAsync(session,x=>x.Id==f.Source.WorkAssignmentId,Builders<WorkAssignment>.Update.Set(x=>x.CurrentReviewerUserId,actor),cancellationToken:ct);
    await session.CommitTransactionAsync(ct);
}
await MoveViewAuthority(f.Outsider);
var moved=(await store.ExecuteAsync((tx,t)=>tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances,instance.Id,t),ct))!.Value;
Check(moved.AuthorityUserId==f.Outsider && Ok(await Controller(f.Outsider).Read(owner,ct)).GetProperty("canEdit").GetBoolean()
    && !Ok(await Controller().Read(owner,ct)).GetProperty("canEdit").GetBoolean(),"handover hook moves standalone edit/refresh authority; historical creator is not current editor");
Check(await new AggregateRefreshService(store,reader).RunAsync(instance.Id,moved.Generation,ct)=="COMPLETED","standalone refresh runs as new responsible user after handover");
await MoveViewAuthority(f.Actor);
await db.WorkAssignmentReports.UpdateOneAsync(x=>x.Id==f.Source.Id,Builders<WorkAssignmentReport>.Update.Set(x=>x.Status,WorkAssignmentReportStatus.Draft),cancellationToken:ct);
var unavailable=Ok(await Controller().Read(owner,ct));
Check(unavailable.GetProperty("result").GetProperty("lastResults").GetArrayLength()==0
    && unavailable.GetProperty("result").GetProperty("resultReadError").GetString()=="AGG_SOURCE_UNAVAILABLE"
    && unavailable.GetProperty("canEdit").GetBoolean(),"source no longer Approved hides results with explicit error while owner can repair configuration");
await db.WorkAssignmentReports.UpdateOneAsync(x=>x.Id==f.Source.Id,Builders<WorkAssignmentReport>.Update.Set(x=>x.Status,WorkAssignmentReportStatus.Approved),cancellationToken:ct);
var rootOwner=new AggregateViewOwnerRequest(f.WorkId,"WORK",f.WorkId);
var rootContext=await Bind(rootOwner,f.TargetForm);
var rootReader=new AggregateMongoPreviewReader(db,payloads,new AggregateMongoDataWindows(db),true);
var rootRead=await rootReader.ReadContextAsync(rootContext,f.Actor,ct);
var forms=await rootReader.ListSourceFormsAsync(rootRead,f.Actor,ct);
Check(rootContext.AssignmentId=="" && rootContext.View!.ViewId!=context.View!.ViewId,"Work and assignment own distinct single view heads");
Check(forms.Count==1 && forms[0].FormId==f.TargetForm.Id,"Work sees only root assignments, excludes grandchildren");
var priorContext=context;
for(var n=0;n<4;n++) {
    var form=new DynamicFormTemplate { Id=ObjectId.GenerateNewId().ToString(),FamilyId=ObjectId.GenerateNewId().ToString(),Name="Replacement "+n,Code=run+n,CreatedByUserId=f.Actor,CreatedByUsername=run,IsPublished=true,SectionsJson=f.TargetForm.SectionsJson,FieldsJson=f.TargetForm.FieldsJson };
    var snapshot=tdtd_be.Services.DynamicForms.DynamicFormPublishedSchemaSnapshotBuilder.Build(form);form.PublishedSchemaHash=snapshot.Sha256;form.PublishedSchemaSnapshotJson=snapshot.Json;
    await db.DynamicFormTemplates.InsertOneAsync(form,cancellationToken:ct);context=await Bind(owner,form);
}
var history=Ok(await Controller().Read(owner,ct)).GetProperty("view").GetProperty("history");
Check(history.GetArrayLength()==3,"changing target keeps current plus at most three previous generations");
try { await reader.AuthorizeAsync(priorContext,f.Actor,run,ct);throw new Exception("expired generation accepted"); } catch(AggregatePreviewException e) when(e.Code=="AGG_CONTEXT_STALE") {Check(true,"evicted generation cannot read or apply");}
Check(await db.DynamicFormTemplates.CountDocumentsAsync(x=>x.Id==f.TargetForm.Id,cancellationToken:ct)==1,"retention never deletes original Form or report");
var collectionFixture = await CollectionChecks.Run(db,runner,config,run,ct);
await FixtureProvenanceChecks.Migration(db,"after view rebind, collections and retention",ct);
if(args.Contains("--browser-fixture")) {
    var user = await db.Users.Find(u=>u.Id==collectionFixture.Actor).SingleAsync(ct);
    user.Username="aggregate_view_test";user.FullName="DỮ LIỆU GIẢ — Kiểm thử tổng hợp";
    user.PasswordHash=new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().HashPassword(user,"LocalView!2026");
    await db.Users.ReplaceOneAsync(u=>u.Id==user.Id,user,cancellationToken:ct);
    await db.DocRoles.InsertOneAsync(new DocRole {Id=ObjectId.GenerateNewId().ToString(),DocId=collectionFixture.WorkId,DocType=DocType.WORK,UserId=user.Id,Role=DocRoleType.OWNER,CreatedByUserId=user.Id},cancellationToken:ct);
    await File.WriteAllTextAsync("outputs/aggregate-view-browser-fixture.json",JsonSerializer.Serialize(new { database=run, workId=collectionFixture.WorkId, assignmentId=collectionFixture.Binding.WorkAssignmentId,username=user.Username },AggregateCanonical.Json),ct);
}

Console.WriteLine($"PASS {checks} checks; private fixtures retained; no browser UAT claim");


