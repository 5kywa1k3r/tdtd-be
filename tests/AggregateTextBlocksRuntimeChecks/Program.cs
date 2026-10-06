// R8 own fixtures. Real Mongo transactions, authoritative Form writer and Hangfire.
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Minio;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

if(args.Length==8 && args.Take(3).SequenceEqual(new[]{"--database","tdtd","--own-materialization-worker"})){
    await TextMaterializationRecoveryChecks.Child(args);return;
}

var auditOnly=args.SequenceEqual(new[]{"--database","tdtd","--audit-missing-template"});
if(args.SequenceEqual(new[]{"--database","tdtd","--audit-completion-date-impact"})){
    using var auditTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await CompletionDateImpactAudit.Run(new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database="tdtd"})),auditTimeout.Token);return;
}
if(args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--audit-own-browser-output"})){
    using var auditTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(1));
    await CompletionDateImpactAudit.Browser(new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database="tdtd"})),args[3],auditTimeout.Token);return;
}
if(args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--audit-own-completion-recovery"})){
    using var auditTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(1));
    await TextCompletionRecoveryChecks.Audit(new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database="tdtd"})),args[3],auditTimeout.Token);return;
}
if(args.SequenceEqual(new[]{"--database","tdtd","--recover-own-completion-fixture"})){
    using var recoveryTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(8));
    await TextCompletionRecoveryChecks.Run(new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database="tdtd"})),recoveryTimeout.Token);return;
}
var inspectScale=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--inspect-own-scale-fixture"});
if(args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--audit-own-arrival-fixture"})){
    using var auditTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await TextApprovalArrivalChecks.Audit(new MongoDbContext(Options.Create(new MongoOptions{ConnectionString="mongodb://localhost:27017/?replicaSet=tdtd-rs",Database="tdtd"})),args[3],auditTimeout.Token);return;
}
var prepareBrowser=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--prepare-own-browser"});
var repairBrowserConfig=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--repair-own-browser-stat-config"});
var restoreAuthorityFixture=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--restore-own-authority-fixture"});
var cleanupPeriodicLifecycle=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--cleanup-own-periodic-lifecycle"});
var auditPeriodicLifecycle=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--audit-own-periodic-lifecycle"});
var withPeriodicLifecycle=args.SequenceEqual(new[]{"--database","tdtd","--run-own-text-fixture","--with-periodic-lifecycle"});
var asyncWide=args.SequenceEqual(new[]{"--database","tdtd","--run-own-wide-fixture","--async-compute"});
var approvalArrival=Environment.GetEnvironmentVariable("AGG_P05_APPROVAL_ARRIVAL")=="1"||Environment.GetEnvironmentVariable("AGG_REPORT_SET_RETURN")=="1";
var periodicScale=approvalArrival||Environment.GetEnvironmentVariable("AGG_P05_PERIODIC_SCALE")=="1";
var wideLoad=asyncWide||args.SequenceEqual(new[]{"--database","tdtd","--run-own-wide-fixture"});
var auditWide=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--audit-own-wide-fixture"});
var inspectWide=args.Length==4&&args.Take(3).SequenceEqual(new[]{"--database","tdtd","--inspect-own-wide-fixture"});
if(!inspectScale&&!inspectWide&&!auditOnly&&!prepareBrowser&&!repairBrowserConfig&&!restoreAuthorityFixture&&!cleanupPeriodicLifecycle&&!auditPeriodicLifecycle&&!withPeriodicLifecycle&&!wideLoad&&!auditWide&&!args.SequenceEqual(new[]{"--database","tdtd","--run-own-text-fixture"}))throw new ArgumentException("Use --database tdtd --run-own-text-fixture [--with-periodic-lifecycle], --run-own-wide-fixture, --audit-own-wide-fixture <own-work-id>, --inspect-own-wide-fixture <own-work-id>, --inspect-own-scale-fixture <own-work-id>, --prepare-own-browser <own-work-id>, --repair-own-browser-stat-config <own-work-id>, --restore-own-authority-fixture <own-run>, --cleanup-own-periodic-lifecycle <own-run>, --audit-own-periodic-lifecycle <own-run> or --audit-missing-template; no reset or blanket cleanup.");
using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(wideLoad||periodicScale?20:
    Environment.GetEnvironmentVariable("AGG_P05_OUTAGE_CHECKS")=="1"?15:
    Environment.GetEnvironmentVariable("AGG_P05_MATERIALIZATION_RECOVERY")=="1"?10:6));var ct=timeout.Token;
const string connection="mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000";
var db=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString=connection,Database="tdtd"}));
if(inspectScale){await TextPeriodicScaleChecks.Inspect(db,args[3],ct);return;}
if(auditWide){await TextWideFixtureAudit.Run(db,args[3],ct);return;}
if(inspectWide){await TextWideFixtureAudit.Inspect(db,args[3],ct);return;}
if(prepareBrowser){await TextBrowserFixture.Prepare(db,args[3],ct);return;}
if(repairBrowserConfig){await TextBrowserFixture.RepairEmptyStatisticConfig(db,args[3],ct);return;}
if(restoreAuthorityFixture){await TextAuthorityChecks.RestorePending(db,args[3],ct);return;}
if(cleanupPeriodicLifecycle){await TextPeriodicLifecycleChecks.CleanupOwn(db,args[3],ct);return;}
if(auditPeriodicLifecycle){await TextPeriodicLifecycleChecks.AuditOwnCleanup(db,args[3],ct);return;}
if(auditOnly){
    var binding=await db.WorkTemplateAssignees.Find(b=>b.Id=="6ac3cc49215962add5c1bb7c").SingleAsync(ct);
    var form=await db.DynamicFormTemplates.Find(f=>f.Id==binding.DynamicFormTemplateId).FirstOrDefaultAsync(ct);
    var work=await db.Works.Find(w=>w.Id==binding.WorkId).FirstOrDefaultAsync(ct);
    var assignment=await db.WorkAssignments.Find(a=>a.Id==binding.WorkAssignmentId).FirstOrDefaultAsync(ct);
    var creator=await db.Users.Find(u=>u.Id==binding.CreatedByUserId).FirstOrDefaultAsync(ct);
    var reports=await db.WorkAssignmentReports.Find(r=>r.WorkId==binding.WorkId&&r.WorkAssignmentId==binding.WorkAssignmentId).ToListAsync(ct);
    var audit=new{mode="READ_ONLY",binding.Id,binding.WorkId,binding.WorkAssignmentId,binding.DynamicFormTemplateId,binding.DynamicFormFamilyId,binding.DynamicFormVersionNo,binding.DynamicFormSchemaHash,binding.IsDeleted,binding.CreatedAtUtc,binding.CreatedByUserId,creatorUsername=creator?.Username,workName=work?.Name,assignmentName=assignment?.Name,templateExists=form!=null,reportPins=reports.Select(r=>new{r.Id,r.DynamicFormTemplateId,r.DynamicFormSchemaHash,r.Status})};
    var json=JsonSerializer.Serialize(audit,new JsonSerializerOptions{WriteIndented=true});Console.WriteLine(json);Directory.CreateDirectory("../outputs/aggregate-operators-20261005");await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/r8-provenance-blocker-readonly.json",json,ct);return;
}
await FixtureProvenanceChecks.Migration(db,"R8 before seed",ct);
var run="r8-text-20261005-"+Guid.NewGuid().ToString("N")[..8];var checks=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
string Id()=>ObjectId.GenerateNewId().ToString();
var table=new DynamicFormNativeTableDto{Id="body",SectionId="main",Name="Đơn vị — Nội dung",Layout="vertical",Order=0,
    Fields=[new(){Id="unit",Name="Đơn vị báo cáo",Order=0},new(){Id="content",Name="Nội dung",Order=1}],Rows=[],StatisticTargets=[],
    TypeConfig=new(){Version=1,Sequence=2,Rules=[new(){Order=1,Target=new(){Scope="column",FieldId="unit"},Spec=new(){Type="plainText",Required=false}},new(){Order=2,Target=new(){Scope="column",FieldId="content"},Spec=new(){Type="plainText",Required=false}}]}};
void ConfigureForm(DynamicFormTemplate form,bool target){
    form.Name="[DỮ LIỆU THỬ RIÊNG R8] "+(target?"Đích":"Nguồn");
    form.FieldsJson=target?"[{\"id\":\"total\",\"key\":\"total\",\"name\":\"Danh sách nội dung\",\"sectionId\":\"main\",\"type\":\"stringList\"},{\"id\":\"plain\",\"key\":\"plain\",\"name\":\"Khối chữ\",\"sectionId\":\"main\",\"type\":\"longText\"}]"
        :"[{\"id\":\"n\",\"key\":\"n\",\"name\":\"Danh sách nội dung nguồn\",\"sectionId\":\"main\",\"type\":\"stringList\"},{\"id\":\"text\",\"key\":\"text\",\"name\":\"Chữ nguồn\",\"sectionId\":\"main\",\"type\":\"longText\"},{\"id\":\"rich\",\"key\":\"rich\",\"name\":\"Chữ có định dạng\",\"sectionId\":\"main\",\"type\":\"richText\"}]";
    if(target){form.NativeTablesVersion=1;form.TablesJson=JsonSerializer.Serialize(new[]{table},AggregateCanonical.Json);}
}
var f=await RuntimeFixture.Seed(db,run,ct,configureForm:ConfigureForm,publishForm:(form,token)=>TextBrowserFixture.Publish(db,form,token));
var payload=new WorkReportPayloadService(db);
var unit1=(await db.WorkTemplateAssignees.Find(b=>b.WorkAssignmentId==f.Source.WorkAssignmentId).SingleAsync(ct)).AssigneeUnitId!;
await db.Units.InsertOneAsync(new Unit{Id=unit1,Code=run+"-u1",FullName="Đơn vị thử R8 Một",ShortName="R8 Một",Symbol="R8M",Note=run,CreatedByUserId=f.Actor},cancellationToken:ct);
var unit2=Id();await db.Units.InsertOneAsync(new Unit{Id=unit2,Code=run+"-u2",FullName="Đơn vị thử R8 Hai",Symbol="R8H",Note=run,CreatedByUserId=f.Actor},cancellationToken:ct);
var sourceUser2=new AppUser{Id=Id(),Username=run+"-source2",FullName="[THỬ RIÊNG R8] Người báo cáo hai",UnitId=unit2,Note=run,CreatedByUserId=f.Actor};
await db.Users.InsertOneAsync(sourceUser2,cancellationToken:ct);
var child=await db.WorkAssignments.Find(a=>a.Id==f.Source.WorkAssignmentId).SingleAsync(ct);var childParent=child.ParentAssignmentId!;
var binding2=await db.WorkTemplateAssignees.Find(b=>b.WorkAssignmentId==child.Id).SingleAsync(ct);
child.Id=Id();child.Code=run+"-child2";child.Path="/"+childParent+"/"+child.Id;child.Assignees=[new(){UserId=sourceUser2.Id,UnitId=unit2}];child.TargetUnitIds=[unit2];
await db.WorkAssignments.InsertOneAsync(child,cancellationToken:ct);
binding2.Id=Id();binding2.WorkAssignmentId=child.Id;binding2.AssigneeUnitId=unit2;binding2.AssigneeUserId=sourceUser2.Id;binding2.AssigneeUsername=sourceUser2.Username;await db.WorkTemplateAssignees.InsertOneAsync(binding2,cancellationToken:ct);
var source2=await db.WorkAssignmentReports.Find(r=>r.Id==f.Source.Id).SingleAsync(ct);source2.Id=Id();source2.WorkAssignmentId=child.Id;source2.WorkReportPeriodId=Id();source2.PeriodInstanceKey=binding2.Id+":ONCE";
source2.AssigneeUserId=sourceUser2.Id;source2.CreatedByUserId=sourceUser2.Id;
var items=new[]{string.Concat(Enumerable.Repeat("Nội dung tiếng Việt 📝 ",80)).Trim(),"Dòng hai: A < B & C"};
var block=string.Join("\n",items);var joined=block+"\n\n"+block;
var sourceFields=DynamicFormRuntimeFieldCanonicalizer.Canonicalize(JsonSerializer.Serialize(new{n=items,text=new string('x',1001),rich="<p><b>đã</b> xử lý</p>"}),[new("n","stringList"),new("text","longText"),new("rich","richText")],false).CanonicalValuesJson;
foreach(var source in new[]{f.Source,source2}){
    var saved=await payload.SaveReportPayloadAsync(source,"[]",sourceFields,null,null,source.AssigneeUserId,DateTime.UtcNow,ct);
    source.PayloadRevision=saved.PayloadRevision;source.PayloadHash=saved.PayloadHash;source.PayloadSizeBytes=saved.PayloadSizeBytes;source.PayloadStatus=saved.PayloadStatus;
    if(source.Id==f.Source.Id)await db.WorkAssignmentReports.ReplaceOneAsync(r=>r.Id==source.Id,source,cancellationToken:ct);else await db.WorkAssignmentReports.InsertOneAsync(source,cancellationToken:ct);
}
await db.WorkReportPeriods.InsertOneAsync(new WorkReportPeriod{Id=source2.WorkReportPeriodId!,WorkId=f.WorkId,WorkAssignmentId=child.Id,WorkTemplateAssigneeId=binding2.Id,AssigneeUserId=source2.AssigneeUserId,AssigneeUnitId=unit2,CurrentReportId=source2.Id,PeriodKey="ONCE",PeriodInstanceKey=source2.PeriodInstanceKey,DynamicFormTemplateId=f.SourceForm.Id,DynamicFormFamilyId=f.SourceForm.FamilyId,DynamicFormVersionNo=f.SourceForm.VersionNo,DynamicFormSchemaHash=f.SourceForm.PublishedSchemaHash,CreatedByUserId=f.Actor},cancellationToken:ct);
await FixtureProvenanceChecks.Migration(db,"R8 after final seed/pins",ct);
var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[],EnvironmentName="TextBlockChecks"});builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
var signing=new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
builder.Configuration.AddJsonFile("appsettings.json",false).AddInMemoryCollection(new Dictionary<string,string?>{["AggregateMapping:V2Enabled"]="true",["AggregateMapping:Enabled"]="false",["AggregateMapping:ConfirmationKeyBase64"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))});
builder.Services.AddSingleton(db);builder.Services.AddSingleton<IDynamicFlowDefinitionTransactionRunner,DynamicFlowDefinitionTransactionRunner>();
if(periodicScale)builder.Services.AddSingleton<IDynamicFlowDefinitionTransactionRunner>(services=>new ScaleDiagnosticTransactions(
    new DynamicFlowDefinitionTransactionRunner(db,services.GetRequiredService<ILogger<DynamicFlowDefinitionTransactionRunner>>())));
builder.Services.AddSingleton<IWorkReportPayloadReader>(payload);builder.Services.AddSingleton<IWorkReportPayloadWriter>(payload);
builder.Services.AddSingleton<IWorkAssignmentReportService>(_=>{var constructor=typeof(WorkAssignmentReportService).GetConstructors().Single();return(WorkAssignmentReportService)constructor.Invoke(constructor.GetParameters().Select(p=>p.ParameterType==typeof(MongoDbContext)?(object)db:null).ToArray());});
builder.Services.AddSingleton<IMinioClient>(_=>new MinioClient().WithEndpoint(builder.Configuration["Minio:Endpoint"]!).WithCredentials(builder.Configuration["Minio:AccessKey"]!,builder.Configuration["Minio:SecretKey"]!).WithSSL(builder.Configuration.GetValue<bool>("Minio:Secure")).Build());
builder.Services.AddHangfire(c=>c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings().UseMongoStorage(new MongoClient(connection),"tdtd",new MongoStorageOptions{Prefix=run.Replace('-','_'),CheckQueuedJobsStrategy=CheckQueuedJobsStrategy.Poll,QueuePollInterval=TimeSpan.FromMilliseconds(200),MigrationOptions=new(){MigrationStrategy=new MigrateMongoMigrationStrategy(),BackupStrategy=new CollectionMongoBackupStrategy()}}));
builder.Services.AddHangfireServer(o=>{o.ServerName=run;o.WorkerCount=periodicScale?3:1;o.SchedulePollingInterval=TimeSpan.FromSeconds(1);o.ShutdownTimeout=TimeSpan.FromSeconds(5);});
builder.Services.AddControllers().AddApplicationPart(typeof(AggregateMappingPreviewController).Assembly);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>o.TokenValidationParameters=new(){ValidateIssuer=true,ValidIssuer=run,ValidateAudience=true,ValidAudience=run,ValidateIssuerSigningKey=true,IssuerSigningKey=signing,ValidateLifetime=true,ClockSkew=TimeSpan.Zero});builder.Services.AddAuthorization();
await using var app=builder.Build();app.Use(async(context,next)=>{try{await next(context);}catch(AppException ex){context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new{code=ex.Code.ToString()});}catch(Exception ex){Console.Error.WriteLine(ex);context.Response.StatusCode=500;await context.Response.WriteAsJsonAsync(new{code="R8_HOST_ERROR"});}});app.UseAuthentication();app.UseAuthorization();app.MapControllers();await app.StartAsync(ct);
try{
    var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();Console.WriteLine("OWN_HOST_READY "+address+" work="+f.WorkId+" target="+f.Report.Id);
    using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri(address+"/api/")};
    string Token(string actor)=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(run,run,[new Claim(JwtRegisteredClaimNames.Sub,actor),new Claim("sid",run+"-session")],DateTime.UtcNow.AddMinutes(-1),DateTime.UtcNow.AddMinutes(6),new SigningCredentials(signing,SecurityAlgorithms.HmacSha256)));
    async Task<JsonElement> Post(string path,object body,string? actor=null,HttpStatusCode expected=HttpStatusCode.OK){using var request=new HttpRequestMessage(HttpMethod.Post,"aggregate-v2/"+path){Content=JsonContent.Create(body,options:AggregateCanonical.Json)};request.Headers.Authorization=new("Bearer",Token(actor??f.Actor));using var response=await http.SendAsync(request,ct);var text=await response.Content.ReadAsStringAsync(ct);if(response.StatusCode!=expected)throw new Exception(path+" expected "+(int)expected+" got "+(int)response.StatusCode+" "+text);return JsonSerializer.Deserialize<JsonElement>(text);}
    var store=new AggregateMongoStore(db,app.Services.GetRequiredService<IDynamicFlowDefinitionTransactionRunner>(),payload,payload);await store.CreateIndexesAsync(ct);
    await TextPinBatchChecks.Run(db,f,run,Check,ct);
    if(Environment.GetEnvironmentVariable("AGG_REPORT_SET_FILTER")=="1"){
        try{await TextReportSetRuntimeChecks.Once(db,run,(path,body,actor,status)=>Post(path,body,actor,status),Check,ct);
            await TextReportSetRuntimeChecks.Content(db,f,run,(path,body,actor,status)=>Post(path,body,actor,status),Check,ct);
            await TextReportSetRuntimeChecks.Run(db,store,run,(path,body,actor,status)=>Post(path,body,actor,status),Check,ct);
            Console.WriteLine($"PASS report-set {checks} assertions; real API/job/writer, not browser UAT.");}
        catch(Exception ex){Console.Error.WriteLine("FAIL report-set: "+ex);Environment.ExitCode=1;}return;
    }
    if(Environment.GetEnvironmentVariable("AGG_P05_MIXED_CALENDAR")=="1"){
        try{await TextMixedCalendarChecks.Run(db,store,run,(path,body,actor,status)=>Post(path,body,actor,status),Check,ct);
            Console.WriteLine($"PASS P05 mixed calendar {checks} assertions; real API/job/writer, not browser UAT.");}
        catch(Exception ex){Console.Error.WriteLine("FAIL P05 mixed calendar: "+ex);Environment.ExitCode=1;}return;
    }
    if(periodicScale){
        try{
            await TextPeriodicScaleChecks.Run(db,store,app.Services.GetRequiredService<IDynamicFlowDefinitionTransactionRunner>(),run,
                ConfigureForm,(path,body,actor,status)=>Post(path,body,actor,status),Check,ct,approvalArrival);
            await FixtureProvenanceChecks.Migration(db,"P05 cumulative concurrent load after readback",ct);
            Console.WriteLine($"PASS P05 periodic scale {checks} assertions; private HTTP/Hangfire/writer, not browser UAT.");
        }catch(Exception ex){Console.Error.WriteLine("FAIL P05 periodic scale: "+ex);Environment.ExitCode=1;}
        return;
    }
    if(wideLoad){
        await WideFormLoadChecks.Run(db,store,app.Services.GetRequiredService<IDynamicFlowDefinitionTransactionRunner>(),builder.Configuration,run,
            (path,body,actor,status)=>Post(path["aggregate-v2/".Length..],body,actor,status),ct,mixedContent:true,
            publishForm:(form,token)=>TextBrowserFixture.Publish(db,form,token),asyncCompute:asyncWide);
        await FixtureProvenanceChecks.Migration(db,"R8 after 166 x 200 wide workload",ct);
        Console.WriteLine("PASS R8 current binary 166 x 200 mixed workload; fixture sources seeded Approved, lifecycle participant seam, no browser or SLA claim.");
        return;
    }
    var declaration=new AggregateDataWindowDeclarationDto("2026-09-01","2026-09-30","USER_DECLARED",run,1);
    await store.ExecuteAsync(async(tx,cancel)=>{foreach(var source in new[]{f.Source,source2}){var period=await db.WorkReportPeriods.Find(p=>p.Id==source.WorkReportPeriodId).SingleAsync(cancel);foreach(var key in new[]{("REPORT",source.Id),("SLOT",period.WorkTemplateAssigneeId+":ONCE")})await tx.PutAsync(AggregateCollections.Declarations,key.Item1+":"+key.Item2,0,new AggregateDeclarationState(key.Item1,key.Item2,f.WorkId,source.WorkAssignmentId,declaration),f.WorkId,key.Item2,[],cancel);}return true;},ct);
    var boot=await Post("editor/bootstrap",new{reportId=f.Report.Id});var context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
    Check(boot.GetProperty("capabilities").GetProperty("textPolicy").GetProperty("shortTextLimit").GetInt32()==1000,"bootstrap publishes real text policy 1000");
    var confirmation=await Post("data-window/preview",new AggregateDeclarationPreviewCommandDto(context,declaration));await Post("data-window/apply",new AggregateDeclarationSaveCommandDto(run+"-window",context,declaration,confirmation.GetString()!));
    boot=await Post("editor/bootstrap",new{reportId=f.Report.Id});context=boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
    AggregateFormPinDto Pin(DynamicFormTemplate form)=>new(form.Id,form.FamilyId!,form.VersionNo,form.PublishedSchemaHash!);
    var sourceSchema=await Post("editor/source-schema",new AggregateEditorSchemaQueryDto(context,Pin(f.SourceForm)));var n=sourceSchema.GetProperty("members").EnumerateArray().Single(m=>m.GetProperty("id").GetString()=="n");Check(n.GetProperty("sourceType").GetString()=="stringList"&&n.GetProperty("valueType").GetString()=="TEXT"&&n.GetProperty("supported").GetBoolean(),"schema exposes stringList as whole-block adapter, not native LIST");
    AggregateExpressionDto Input()=>new(){Kind="INPUT",Ref="in"};
    var recipe=new AggregateRecipeDto{SchemaVersion=1,SemanticProfile="REPORT_MAPPING_V1",TimeRules=[new(){Id="w",Mode="TARGET_DATA_WINDOW",SourceDateBasis="DECLARED_DATA_WINDOW",Match="CONTAINED"}],Nodes=[
        new(){Id="s",Kind="SOURCE",Form=Pin(f.SourceForm),Origin="DIRECT_CHILD_REPORTS",SourceCardinality="SET",Inputs=[],Outputs=[new("out","TEXT","SET","n","w")]},
        new(){Id="c",Kind="CALCULATION",Inputs=[new("in","TEXT","SET")],Outputs=[new("text","TEXT","SINGLE"),new("table","TABLE","SINGLE")],Expressions=[new("text",new(){Kind="CALL",Name="CONCAT",Arguments=[Input()],Options=new(){Order="UNIT_THEN_PERIOD",Separator="ignored",Trim=true}}),new("table",new(){Kind="CALL",Name="REPORT_TEXT_TABLE",Arguments=[Input()],Options=new(){Order="UNIT_THEN_PERIOD",UnitDisplay="SHORT_NAME"}})]},
        new(){Id="t",Kind="TARGET",Form=Pin(f.TargetForm),Inputs=[new("blocks","TEXT","SINGLE","total"),new("plain","TEXT","SINGLE","plain"),new("table","TABLE","SINGLE","body")],Outputs=[]}],Edges=[new("a",new("s","out"),new("c","in")),new("b",new("c","text"),new("t","blocks")),new("p",new("c","text"),new("t","plain")),new("tbl",new("c","table"),new("t","table"))]};
    var config=await Post("configs",new AggregateConfigCreateCommandDto(context,new(run+"-config",f.Binding.Id,Pin(f.TargetForm),recipe)));var configId=config.GetProperty("id").GetString()!;
    var instance=await Post("instances",new AggregateInstanceCreateCommandDto(run+"-instance",context,configId));var instanceId=instance.GetProperty("id").GetString()!;
    var change=new AggregateMappingChangeDto(1,null,new([new("s","FORM_SELECTOR",[],[])]),[],false);
    async Task<JsonElement> Job(AggregateMappingChangeDto next){var job=await Post("preview-jobs/start",new AggregatePreviewJobRequest("MAPPING",context,null,instanceId,next));var key=job.GetProperty("id").GetString()!;while(job.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){await Task.Delay(200,ct);job=await Post("preview-jobs/"+key+"/read",new{});}return job;}
    var before=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);var job=await Job(change);Check(job.GetProperty("state").GetString()=="COMPLETED","real Hangfire whole-block preview completed: "+job.GetProperty("errorCode"));
    var jobId=job.GetProperty("id").GetString()!;var prepared=job.GetProperty("result");var results=prepared.GetProperty("preview").GetProperty("preview").GetProperty("results").EnumerateArray().ToArray();
    Check(results.Single(r=>r.GetProperty("portId").GetString()=="blocks").GetProperty("value").GetString()==joined,"preview keeps both equal report blocks and system separation");
    var reference=results.Single(r=>r.GetProperty("portId").GetString()=="table").GetProperty("value");var content=new AggregateContentReadRequest(f.Report.Id,null,null,jobId,reference);
    var page=await Post("content/page",content);var rows=page.GetProperty("rows").EnumerateArray().ToArray();Check(rows.Length==2&&rows.Any(r=>r.GetProperty("unitName").GetString()=="R8 Một")&&rows.Any(r=>r.GetProperty("unitName").GetString()==""),"API table uses chosen short name; missing label stays blank");
    Check(rows.All(r=>r.GetProperty("sample").GetString()!.Length<=240&&r.GetProperty("hasMore").GetBoolean()),"long table pages return bounded samples");
    Check((await Post("content/part",content with{RowKey=rows[0].GetProperty("rowKey").GetString()})).GetProperty("text").GetString()==block,"part API reads complete per-report block");
    await Post("content/page",content,f.Outsider,HttpStatusCode.NotFound);Check(true,"preview content does not grant outsider access");
    Check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==before.PayloadRevision,"preview did not write report");
    var apply=new AggregateMappingApplyCommandDto(run+"-apply",context,change,prepared.GetProperty("token").GetString()!);await Post("instances/"+instanceId+"/apply",apply);
    var saved=await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct);var snapshot=await payload.LoadReportPayloadAsync(saved,ct);var fields=JsonSerializer.Deserialize<JsonElement>(snapshot.FieldValuesJson!);
    Check(fields.GetProperty("total").ValueKind==JsonValueKind.Array&&fields.GetProperty("total").GetArrayLength()==1&&fields.GetProperty("total")[0].GetString()==joined,"authoritative Apply keeps native string[] with one complete aggregate block");
    Check(fields.GetProperty("plain").GetString()==joined&&saved.PayloadRevision==before.PayloadRevision+1,"plain Apply/readback matches and increments once");
    var canonical=DynamicFormRuntimeFieldCanonicalizer.Canonicalize(snapshot.FieldValuesJson,[new("total","stringList"),new("plain","longText")],false);Check(AggregateCanonical.Text(JsonSerializer.Deserialize<JsonElement>(canonical.CanonicalValuesJson))==AggregateCanonical.Text(fields),"readback passes unchanged Form canonicalizer");
    var reportContent=content with{JobId=null,TableId="body",PayloadRevision=saved.PayloadRevision};Check((await Post("content/page",reportContent)).GetProperty("rows").GetArrayLength()==2,"saved report table reads both source contributions");
    var submitBoot=await Post("editor/bootstrap",new{reportId=f.Report.Id});
    var submitContext=submitBoot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
    var submission=await Post("reports/"+f.Report.Id+"/submission-preview",new AggregateInstanceReadCommandDto(submitContext));
    var submissionReference=submission.GetProperty("instances")[0].GetProperty("preview").GetProperty("results").EnumerateArray().Single(r=>r.GetProperty("portId").GetString()=="table").GetProperty("value");
    var submissionContent=new AggregateContentReadRequest(f.Report.Id,null,null,null,submissionReference,ListReadId:submission.GetProperty("listReadId").GetString());
    var submissionPage=await Post("content/page",submissionContent);
    Check(submissionPage.GetProperty("rows").GetArrayLength()==2,"submission preview's existing read scope authorizes content page");
    Check((await Post("content/part",submissionContent with{RowKey=submissionPage.GetProperty("rows")[0].GetProperty("rowKey").GetString()})).GetProperty("text").GetString()==block,"submission read scope authorizes the full report block");
    await Post("content/page",submissionContent,f.Outsider,HttpStatusCode.NotFound);Check(true,"submission content scope rejects another actor");
    await Post("content/page",submissionContent with{ReportId=f.Source.Id},expected:HttpStatusCode.NotFound);Check(true,"submission content scope rejects another target report");
    var active=AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceId)).SingleAsync(ct)).Value;
    var stale=await Post("instances/"+instanceId+"/apply",apply with{CommandId=run+"-stale",Change=change with{ExpectedRevision=active.Revision}},expected:HttpStatusCode.Conflict);Check(stale.GetProperty("code").GetString()=="AGG_CONFIRMATION_STALE","old Apply token rejected after revision: "+stale.GetProperty("code").GetString());
    Check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==saved.PayloadRevision,"stale confirmation does not write payload");
    // Known unsupported content operation: one explicit report avoids cardinality masking.
    var invalid=new AggregateExpressionDto{Kind="CALL",Name="IF",Arguments=[new(){Kind="CALL",Name="TEXT_CONTAINS",Arguments=[new(){Kind="CALL",Name="ONLY",Arguments=[Input()]},new(){Kind="TEXT",Value="Nội dung"}]},recipe.Nodes[1].Expressions![0].Expression,recipe.Nodes[1].Expressions![0].Expression]};
    var overrides=new AggregateInstanceOverrideDto(AggregateCanonical.Hash(recipe),[],[new("c","text",invalid)],[]);
    var failed=await Job(change with{ExpectedRevision=active.Revision,Overrides=overrides,Selection=new([new("s","EXPLICIT_REPORTS",[f.Source.Id],[])])});
    Check(failed.GetProperty("state").GetString()=="FAILED"&&failed.GetProperty("errorCode").GetString()=="AGG_TEXT_BLOCK_CONCAT_REQUIRED","job rejects text search on whole-block stringList");
    Check(failed.GetProperty("errorLocation").GetProperty("nodeId").GetString()=="c","policy failure preserves calculation location");
    Check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==saved.PayloadRevision,"failed preview preserves applied payload");
    foreach(var source in new[]{f.Source,source2})Check((await payload.LoadReportPayloadAsync(source,ct)).FieldValuesJson==sourceFields,"Approved source payload unchanged "+source.Id);
    // R8 policy checks use a fresh RECIPE preview and never alter the saved mapping.
    async Task<JsonElement> RawJob(AggregateRecipeDto graph,AggregateInstanceSelectionDto? selection=null){
        var bootstrap=await Post("editor/bootstrap",new{reportId=f.Report.Id});
        var ctx=bootstrap.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var expected=bootstrap.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
        var request=new AggregatePreviewRequestDto(ctx,"r8-draft","r8-draft-config",expected,graph,selection??change.Selection);
        var next=await Post("preview-jobs/start",new AggregatePreviewJobRequest("RECIPE",ctx,request,null,null));
        while(next.GetProperty("state").GetString() is "QUEUED" or "RUNNING"){
            await Task.Delay(150,ct);next=await Post("preview-jobs/"+next.GetProperty("id").GetString()+"/read",new{});
        }
        return next;
    }
    foreach(var mode in new[]{"FULL_NAME","SYMBOL","NONE"}){
        var graph=recipe with{Nodes=recipe.Nodes.Select(node=>node.Kind=="CALCULATION"?node with{
            Expressions=node.Expressions!.Select(output=>output.PortId=="table"?output with{
                Expression=output.Expression with{Options=output.Expression.Options! with{UnitDisplay=mode}}}:output).ToList()}:node).ToList()};
        var modeJob=await RawJob(graph);Check(modeJob.GetProperty("state").GetString()=="COMPLETED","unit display preview "+mode);
        var result=modeJob.GetProperty("result").GetProperty("preview").GetProperty("results").EnumerateArray().Single(r=>r.GetProperty("portId").GetString()=="table");
        var modePage=await Post("content/page",new AggregateContentReadRequest(f.Report.Id,null,null,modeJob.GetProperty("id").GetString(),result.GetProperty("value")));
        var labels=modePage.GetProperty("rows").EnumerateArray().Select(row=>row.GetProperty("unitName").GetString()).Order().ToArray();
        var expectedLabels=mode=="FULL_NAME"?new[]{"Đơn vị thử R8 Hai","Đơn vị thử R8 Một"}:mode=="SYMBOL"?new[]{"R8H","R8M"}:new[]{"",""};
        Check(labels.SequenceEqual(expectedLabels.Order()),"chosen unit display label/readback "+mode);
    }
    AggregateExpressionDto Call(string name,params AggregateExpressionDto[] arguments)=>new(){Kind="CALL",Name=name,Arguments=arguments.ToList()};
    AggregateExpressionDto Text(string value)=>new(){Kind="TEXT",Value=value};
    AggregateRecipeDto ScalarRecipe(string member,AggregateExpressionDto expression)=>recipe with{
        SchemaVersion=3,SemanticProfile="REPORT_MAPPING_EXTENDED_V1",
        Nodes=[recipe.Nodes[0] with{Outputs=[new("out","TEXT","SET",member,"w")]},
            recipe.Nodes[1] with{Outputs=[new("text","TEXT","SINGLE")],Expressions=[new("text",expression)]},
            recipe.Nodes[2] with{Inputs=[new("plain","TEXT","SINGLE","plain")]}],
        Edges=[new("a",new("s","out"),new("c","in")),new("p",new("c","text"),new("t","plain"))]};
    var single=new AggregateInstanceSelectionDto([new("s","EXPLICIT_REPORTS",[f.Source.Id],[])]);
    var visibleSearch=Call("IF",Call("TEXT_CONTAINS",Call("ONLY",Input()),Text("đã xử lý")),Text("Khớp chữ hiển thị"),Text("Không khớp"));
    var shortRich=await RawJob(ScalarRecipe("rich",visibleSearch),single);
    Check(shortRich.GetProperty("state").GetString()=="COMPLETED"&&shortRich.GetProperty("result").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString()=="Khớp chữ hiển thị","rich text search removes tags across adjacent formatting boundaries");
    var longConcat=await RawJob(ScalarRecipe("text",Call("CONCAT",Input()) with{Options=new(){Order="UNIT_THEN_PERIOD",Separator="\n\n",Trim=false}}));
    Check(longConcat.GetProperty("state").GetString()=="COMPLETED"&&longConcat.GetProperty("result").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString()==new string('x',1001)+"\n\n"+new string('x',1001),"long scalar text CONCAT returns both complete report blocks");
    foreach(var unsupported in new[]{("text","TEXT_CONTAINS","AGG_LONG_TEXT_CONCAT_REQUIRED"),("rich","COUNT_DISTINCT","AGG_RICH_TEXT_DISTINCT_UNSUPPORTED"),("text","LEN","AGG_LONG_TEXT_CONCAT_REQUIRED"),("text","TRIM","AGG_LONG_TEXT_CONCAT_REQUIRED")}){
        var expression=unsupported.Item2=="TEXT_CONTAINS"?Call("IF",Call("TEXT_CONTAINS",Call("ONLY",Input()),Text("x")),Text("có"),Text("không"))
            :unsupported.Item2=="COUNT_DISTINCT"?Call("IF",new(){Kind="BINARY",Name=">",Arguments=[Call("COUNT_DISTINCT",Input()) with{Options=new(){Trim=false,CaseSensitive=false}},new(){Kind="NUMBER",Value="0"}]},Text("có"),Text("không"))
            :unsupported.Item2=="LEN"?Call("IF",new(){Kind="BINARY",Name=">",Arguments=[Call("LEN",Call("ONLY",Input())),new(){Kind="NUMBER",Value="0"}]},Text("có"),Text("không"))
            :Call("TRIM",Call("ONLY",Input()));
        var rejected=await RawJob(ScalarRecipe(unsupported.Item1,expression),single);
        Check(rejected.GetProperty("state").GetString()=="FAILED"&&rejected.GetProperty("errorCode").GetString()==unsupported.Item3,"real job rejects "+unsupported.Item1+" / "+unsupported.Item2+": "+rejected.GetProperty("errorCode"));
        Check(rejected.GetProperty("errorLocation").GetProperty("nodeId").GetString()=="c","policy rejection location "+unsupported.Item2);
    }
    Check((await db.WorkAssignmentReports.Find(r=>r.Id==f.Report.Id).SingleAsync(ct)).PayloadRevision==saved.PayloadRevision,"all policy/unit-label recipe previews preserve saved report revision");
    await TextPeriodicChecks.Run(db,store,run,recipe,ConfigureForm,Post,Check,ct,withPeriodicLifecycle);
    // Recovery/outage checks wait for real leases and may exceed the five-minute
    // read scope. Issue fresh evidence so this tests revision invalidation, not expiry.
    var freshSubmission=await Post("reports/"+f.Report.Id+"/submission-preview",new AggregateInstanceReadCommandDto(submitContext));
    var freshReference=freshSubmission.GetProperty("instances")[0].GetProperty("preview").GetProperty("results").EnumerateArray().Single(r=>r.GetProperty("portId").GetString()=="table").GetProperty("value");
    var freshSubmissionContent=new AggregateContentReadRequest(f.Report.Id,null,null,null,freshReference,ListReadId:freshSubmission.GetProperty("listReadId").GetString());
    await Post("content/page",freshSubmissionContent);
    var latestChange=change with{ExpectedRevision=active.Revision};
    var finalPreview=await Post("instances/"+instanceId+"/mapping/preview",new AggregateMappingPreviewCommandDto(submitContext,latestChange));
    await Post("instances/"+instanceId+"/apply",new AggregateMappingApplyCommandDto(run+"-invalidate-read-scope",submitContext,latestChange,finalPreview.GetProperty("token").GetString()!));
    var staleRead=await Post("content/page",freshSubmissionContent,expected:HttpStatusCode.Conflict);
    Check(staleRead.GetProperty("code").GetString()=="AGG_INPUT_STALE","submission content scope rejects a changed report/mapping revision");
    var authorityInstance=AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceId)).SingleAsync(ct)).Value;
    await TextAuthorityChecks.Run(db,store,payload,f,source2,recipe,submitContext,instanceId,
        change with{ExpectedRevision=authorityInstance.Revision},run,Post,RawJob,Check,ct);
    await FixtureProvenanceChecks.Migration(db,"R8 after runtime/failure",ct);
    Directory.CreateDirectory("../outputs/aggregate-operators-20261005");await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/text-blocks-"+run+".json",JsonSerializer.Serialize(new{run,state="PASS",checks,workId=f.WorkId,reportId=f.Report.Id,sourceReportIds=new[]{f.Source.Id,source2.Id},configId,instanceId,jobId,failedJobId=failed.GetProperty("id").GetString()},new JsonSerializerOptions{WriteIndented=true}),ct);
    Console.WriteLine("PASS: "+checks+" real API/job/writer assertions; "+(withPeriodicLifecycle
        ?"normal API target lifecycle and one source lifecycle checked; remaining sources seeded Approved; no browser UAT."
        :"source seeded Approved, no browser lifecycle UAT."));
}finally{await app.StopAsync(CancellationToken.None);}
