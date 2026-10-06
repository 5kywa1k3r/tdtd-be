using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Fresh, scoped fixture. Count real worker payload reads; do not replace its evaluator,
// lifecycle hooks or authority. Bursts explicitly pause consumption, not event publication.
internal static class TextApprovalArrivalChecks
{
    internal static async Task Audit(MongoDbContext db,string workId,CancellationToken ct)
    {
        var work=await db.Works.Find(w=>w.Id==workId&&!w.IsDeleted).SingleAsync(ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(work.AutoCode,"^r8-text-20261005-[a-f0-9]{8}-scale$"))throw new InvalidOperationException("Not an own fixture.");
        var all=await db.WorkAssignmentReports.Find(r=>r.WorkId==workId&&!r.IsDeleted).ToListAsync(ct);
        var sources=all.Where(r=>r.AssigneeUserId!=work.CreatedByUserId).ToArray();var target=all.Single(r=>r.AssigneeUserId==work.CreatedByUserId);
        if(sources.Length!=166||sources.Count(r=>r.Status==WorkAssignmentReportStatus.Approved)!=165||sources.Count(r=>r.Status==WorkAssignmentReportStatus.Draft)!=1)
            throw new InvalidOperationException("Arrival final source inventory differs.");
        var payload=new WorkReportPayloadService(db);var ordinals=new HashSet<int>();
        foreach(var source in sources){var snapshot=await payload.LoadReportPayloadAsync(source,ct);WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(source,snapshot);
            var fields=JsonSerializer.Deserialize<JsonElement>(snapshot.FieldValuesJson!);var ordinal=fields.GetProperty("score").GetInt32();
            if(!ordinals.Add(ordinal)||ordinal<1||ordinal>166||source.PayloadRevision!=(ordinal==1?2:1)||fields.GetProperty("n")[0].GetString()!=$"Đơn vị {ordinal}, tháng 9"||fields.GetProperty("n")[1].GetString()!=new string('x',1001))
                throw new InvalidOperationException("Source payload/revision changed: "+source.Id);
        }
        if(target.Status!=WorkAssignmentReportStatus.Draft||all.SelectMany(r=>r.LifecycleProjectionOutbox).Any(e=>e.State!=WorkReportLifecycleProjectionOutboxStates.Completed))
            throw new InvalidOperationException("Unexpected final lifecycle/outbox.");
        var fieldsTarget=JsonSerializer.Deserialize<JsonElement>((await payload.LoadReportPayloadAsync(target,ct)).FieldValuesJson!);
        if(fieldsTarget.GetProperty("metric").GetInt32()!=13695)throw new InvalidOperationException("Final target result differs.");
        await FixtureProvenanceChecks.Migration(db,"P05 arrival read-only final audit",ct);
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-arrival-audit-"+workId+".json",JsonSerializer.Serialize(new{state="PASS",mode="READ_ONLY",workId,targetId=target.Id,
            sources=166,approved=165,draft=1,allSourcePayloadsAndInitialRevisionsVerified=true,allOutboxCompleted=true,target.PayloadRevision,target.PayloadHash},new JsonSerializerOptions{WriteIndented=true}),ct);
        Console.WriteLine("PASS arrival read-only audit: 166 exact payloads/initial revisions, 165 Approved + 1 Draft, target sum13695, completed outbox and provenance.");
    }
    internal static async Task Run(MongoDbContext db,RuntimeFixture f,string run,WorkAssignmentReport target,
        IReadOnlyList<string> sourceIds,AggregateRecipeDto recipe,Action<bool,string> check,CancellationToken ct,bool returnFocus=false)
    {
        var work=await db.Works.Find(w=>w.Id==f.WorkId&&w.AutoCode==run+"-scale").SingleAsync(ct);
        var actor=await db.Users.Find(u=>u.Id==f.Actor).SingleAsync(ct);
        if(actor.PasswordHash!=null||!actor.Username.StartsWith(work.AutoCode+"-",StringComparison.Ordinal))throw new InvalidOperationException("Not a fresh own account.");
        var password=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var hash=new PasswordHasher<AppUser>().HashPassword(actor,password);
        if((await db.Users.UpdateOneAsync(u=>u.Id==actor.Id&&u.PasswordHash==null,Builders<AppUser>.Update.Set(u=>u.PasswordHash,hash),cancellationToken:ct)).ModifiedCount!=1)
            throw new InvalidOperationException("Own account CAS failed.");
        var projection=new DocRoleReadModelProjectionService(db);var roles=new DocRoleService(db,projection);
        await roles.UpsertWorkRootRolesAsync(work,ct);
        foreach(var assignment in await db.WorkAssignments.Find(a=>a.WorkId==f.WorkId&&!a.IsDeleted).ToListAsync(ct))await roles.UpsertWorkAssignmentRolesAsync(assignment,ct);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(f.WorkId,f.Actor,ct);
        await projection.RebuildWorkAssignmentsAsync(f.WorkId,f.Actor,ct);await projection.RebuildWorkReportPeriodsAsync(f.WorkId,f.Actor,ct);
        await FixtureProvenanceChecks.Migration(db,"P05 approval arrival before normal host",ct);
        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        var prefix="p05_arrival_"+run.Replace('-','_');
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,CreateNoWindow=true,
            WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        foreach(var pair in new Dictionary<string,string>{["ASPNETCORE_ENVIRONMENT"]="Development",["DOTNET_ENVIRONMENT"]="Development",
            ["ASPNETCORE_URLS"]="http://127.0.0.1:"+port,["Mongo__ConnectionString"]="mongodb://localhost:27017/?replicaSet=tdtd-rs",["Mongo__Database"]="tdtd",
            ["Hangfire__ServerEnabled"]="false",["Hangfire__DashboardEnabled"]="false",["Hangfire__RecurringRegistrationEnabled"]="false",["Hangfire__Prefix"]=prefix,
            ["Redis__Enabled"]="false",["Frontend__Enabled"]="false",["Jwt__Issuer"]=run,["Jwt__Audience"]=run,["Jwt__Key"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["AggregateMapping__V2Enabled"]="true",["AggregateMapping__ConfirmationKeyBase64"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))})start.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(start)??throw new InvalidOperationException("Own approval host failed.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}/api/"),Timeout=TimeSpan.FromSeconds(45)};
        var oldStorage=JobStorage.Current;var oldActivator=JobActivator.Current;var samples=new List<object>();var events=new List<object>();var outcome="FAILED";var local=0;
        var path=Path.Combine(AppContext.BaseDirectory,"p05-approval-arrival-"+run+".json");
        void Check(bool condition,string label){check(condition,"P05 arrival "+label);local++;}
        async Task<JsonElement> Post(string route,object body){using var response=await http.PostAsJsonAsync(route,body,AggregateCanonical.Json,ct);
            var text=await response.Content.ReadAsStringAsync(ct);if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new InvalidOperationException(route+" "+response.StatusCode+" "+text);
            var value=JsonSerializer.Deserialize<JsonElement>(text);if(value.TryGetProperty("lifecycleProjectionPending",out var pending)&&pending.ValueKind==JsonValueKind.True)
                throw new InvalidOperationException("Native lifecycle projection pending: "+route);return value;}
        Task<WorkAssignmentReport> Report(string id)=>db.WorkAssignmentReports.Find(r=>r.Id==id&&r.WorkId==f.WorkId).SingleAsync(ct);
        string instanceId="",configId="";
        async Task<AggregateInstanceState> Instance()=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceId)).SingleAsync(ct)).Value;
        async Task<AggregateRefreshIntent> Intent(){var instance=await Instance();return AggregateMongoTransaction.Read<AggregateRefreshIntent>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Refresh)
            .Find(new BsonDocument("_id",AggregateRefreshService.IntentKey(instanceId,instance.Generation))).SingleAsync(ct)).Value;}
        try{
            var ready=false;for(var i=0;i<120&&!process.HasExited;i++){try{using var ping=await http.GetAsync("../health/ready",ct);if(ping.IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}await Task.Delay(500,ct);}
            if(!ready)throw new InvalidOperationException("Own normal host failed readiness.");
            var login=await Post("auth/login",new{username=actor.Username,password});http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",login.GetProperty("accessToken").GetString());
            var config=new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string,string?>{["AggregateMapping:V2Enabled"]="true"}).Build();
            var payload=new WorkReportPayloadService(db);var counted=new CountedPayloadReader(payload,sourceIds);
            var runner=new DynamicFlowDefinitionTransactionRunner(db,NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
            using var minio=new MinioClient().WithEndpoint(config["Minio:Endpoint"]!).WithCredentials(config["Minio:AccessKey"]!,config["Minio:SecretKey"]!).WithSSL(config.GetValue<bool>("Minio:Secure")).Build();
            using var services=new ServiceCollection().AddSingleton(db).AddSingleton<IConfiguration>(config).AddSingleton<IWorkReportPayloadReader>(counted)
                .AddSingleton<IWorkReportPayloadWriter>(payload).AddSingleton<IDynamicFlowDefinitionTransactionRunner>(runner).AddSingleton<IMinioClient>(minio).BuildServiceProvider();
            var storage=new MongoStorage(db.Db.Client,"tdtd",new MongoStorageOptions{Prefix=prefix,CheckQueuedJobsStrategy=CheckQueuedJobsStrategy.Poll,QueuePollInterval=TimeSpan.FromMilliseconds(100),
                MigrationOptions=new(){MigrationStrategy=new MigrateMongoMigrationStrategy(),BackupStrategy=new CollectionMongoBackupStrategy()}});
            JobStorage.Current=storage;JobActivator.Current=new PrivateActivator(services);
            async Task<AggregatePeriodContextDto> Context()=>(await Post("aggregate-v2/editor/bootstrap",new{reportId=target.Id})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            var context=await Context();var form=new AggregateFormPinDto(f.TargetForm.Id,f.TargetForm.FamilyId!,f.TargetForm.VersionNo,f.TargetForm.PublishedSchemaHash!);
            configId=(await Post("aggregate-v2/configs",new AggregateConfigCreateCommandDto(context,new(run+"-arrival",f.Binding.Id,form,recipe)))).GetProperty("id").GetString()!;
            instanceId=(await Post("aggregate-v2/instances",new AggregateInstanceCreateCommandDto(run+"-arrival-instance",context,configId))).GetProperty("id").GetString()!;
            var selection=new AggregateInstanceSelectionDto([new("s","FORM_SELECTOR",[],[])]);
            async Task Queue(string label,AggregateReportSetDto? filter=null){var instance=await Instance();context=await Context();
                var overlay=filter==null?null:new AggregateInstanceOverrideDto(AggregateCanonical.Hash(recipe),[recipe.TimeRules[0] with{ReportSet=filter}],[],[]);
                var change=new AggregateMappingChangeDto(instance.Revision,overlay,selection,[],false);
                var token=(await Post($"aggregate-v2/instances/{instanceId}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change))).GetProperty("token").GetString()!;
                var watch=Stopwatch.StartNew();await Post($"aggregate-v2/instances/{instanceId}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-"+label,context,change,token));
                events.Add(new{label,ackMs=watch.ElapsedMilliseconds});}
            async Task Materialize(string label,int count,decimal? sum){
                var before=await Report(target.Id);var beforeGeneration=(await Instance()).Generation;
                Check((await Intent()).State=="PENDING",label+" has durable pending intent without editor resave");
                counted.Calls.Clear();var watch=Stopwatch.StartNew();
                using(var server=new BackgroundJobServer(new BackgroundJobServerOptions{ServerName=run+"-arrival",WorkerCount=1,SchedulePollingInterval=TimeSpan.FromSeconds(1),ShutdownTimeout=TimeSpan.FromSeconds(5)},storage)){
                    while(true){var intent=await Intent();if(intent.State=="COMPLETED"||count==0&&intent.State=="FAILED"){watch.Stop();break;}if(intent.State is "FAILED" or "CANCELLED" or "DISCARDED")throw new InvalidOperationException(label+" "+intent.State+" "+intent.ErrorCode);
                        if(watch.Elapsed>TimeSpan.FromMinutes(3))throw new TimeoutException(label);await Task.Delay(100,ct);}
                }
                var elapsed=watch.ElapsedMilliseconds;var current=await Report(target.Id);var state=await Instance();var saved=await payload.LoadReportPayloadAsync(current,ct);
                if(count==0&&(await Intent()).State=="FAILED"){
                    var reader=new AggregateMongoCommandReader(db,payload,true);var authority=await reader.AuthorizeAsync(await Context(),f.Actor,run+"-zero-diagnostic",ct);
                    var diagnostic=await reader.PreviewAsync(state,recipe,authority,ct);
                    Check(current.PayloadRevision==before.PayloadRevision&&current.PayloadHash==before.PayloadHash&&counted.Calls.Count==0,"zero approved keeps saved configuration and original target without reading Submitted payloads");
                    samples.Add(new{label,contributors=0,elapsedMs=elapsed,state="FAILED",error=(await Intent()).ErrorCode,sourcePayloadCalls=0,distinctSourcePayloads=0,diagnostic.Preview});
                    Console.WriteLine("ARRIVAL_ZERO "+JsonSerializer.Serialize(new{diagnostic.Preview.State,diagnostic.Preview.Issues,coverage=diagnostic.Preview.Coverage.GroupBy(s=>s.State).Select(g=>new{state=g.Key,count=g.Count()})},AggregateCanonical.Json));
                    throw new InvalidOperationException("Declared-period arrival baseline must complete at zero Approved; inspect diagnostic evidence.");
                }
                var fields=JsonSerializer.Deserialize<JsonElement>(saved.FieldValuesJson!);var actual=fields.TryGetProperty("metric",out var number)&&number.ValueKind==JsonValueKind.Number?number.GetDecimal():(decimal?)null;
                Check(actual==sum,label+" exact scalar including no-result, not invented zero");
                Check(current.PayloadRevision==before.PayloadRevision+1&&state.Generation==beforeGeneration,label+" publishes one payload and preserves configured generation");
                Check(counted.Calls.Keys.Count==count,label+" measures real distinct contributing source payload reads");
                Check(state.Applied!.Preview.ContributingSources.Select(p=>p.ReportId).Distinct().Count()==count,label+" exact Approved contributors");
                if(count>0){var content=JsonSerializer.Deserialize<JsonElement>(saved.TableValuesJson!).GetProperty("nativeTables").GetProperty("tables").EnumerateArray().Single(t=>t.GetProperty("tableId").GetString()=="body").GetProperty("contentRef");
                var page=await Post("aggregate-v2/content/page",new AggregateContentReadRequest(current.Id,"body",current.PayloadRevision,null,content));
                Check(page.GetProperty("total").GetInt32()==count&&page.GetProperty("rows").GetArrayLength()<=50,label+" paged content readback");}
                samples.Add(new{label,contributors=count,elapsedMs=elapsed,sourcePayloadCalls=counted.Calls.Values.Sum(),distinctSourcePayloads=counted.Calls.Count,actual,current.PayloadRevision,state.Generation,attempt=(await Intent()).Attempt});
                Console.WriteLine($"ARRIVAL_MEASURE {label} count={count} ms={elapsed} uniqueReads={counted.Calls.Count} calls={counted.Calls.Values.Sum()}");
            }
            var serial=0;
            (string Id, ReturnReportRequest Request)? lastReturn=null;
            async Task Review(int index,string operation){var source=await Report(sourceIds[index]);var command=run+"-arrival-"+(++serial);
                object body=operation=="approve"?new ApproveReportRequest{CommandId=command,ExpectedPayloadRevision=source.PayloadRevision,ExpectedLifecycleRevision=source.LifecycleRevision,Comment="Thử riêng P05 nguồn đến dần"}
                    :operation is "deactivate" or "reactivate"?new ReportActiveRequest{CommandId=command,ExpectedPayloadRevision=source.PayloadRevision,ExpectedLifecycleRevision=source.LifecycleRevision,Comment="Thử riêng P05 ẩn hiện"}
                    :new ReturnReportRequest{CommandId=command,ExpectedPayloadRevision=source.PayloadRevision,ExpectedLifecycleRevision=source.LifecycleRevision,Comment="Thử riêng P05 trả nguồn"};
                var watch=Stopwatch.StartNew();await Post("work-assignment-review/reports/"+source.Id+"/"+operation,body);var updated=await Report(source.Id);
                if(operation=="return")lastReturn=(source.Id,(ReturnReportRequest)body);
                Check(updated.PayloadHash==source.PayloadHash,"native "+operation+" preserves source payload "+index);
                events.Add(new{operation,index,ackMs=watch.ElapsedMilliseconds,state=updated.Status.ToString(),updated.IsActive});}
            await Queue("configure-before-approved");await Materialize("zero-approved",0,null);
            var generation=(await Instance()).Generation;
            await Review(0,"approve");await Materialize("first-approved",1,1);
            await Review(1,"approve");await Materialize("second-approved",2,3);
            if(returnFocus){
                await Review(2,"approve");await Materialize("third-approved",3,6);
                await Review(2,"return");
                var pendingRead=await Post($"aggregate-v2/instances/{instanceId}/read",new AggregateInstanceReadCommandDto(await Context()));
                Check(pendingRead.GetProperty("lastResults").GetArrayLength()==0&&pendingRead.GetProperty("resultReadError").GetString()=="AGG_INPUT_STALE",
                    "readback hides stale contribution while returned-source job is pending");
                await Materialize("direct-return-approved",2,3);
                var returned=await Report(sourceIds[2]);
                Check(returned.Status==WorkAssignmentReportStatus.Draft&&returned.ReturnedByUserId==f.Actor&&returned.ReturnedAtUtc.HasValue,
                    "assigning reviewer returned Approved report directly to Draft");
                var targetBeforeReplay=await Report(target.Id);
                await Post("work-assignment-review/reports/"+lastReturn!.Value.Id+"/return",lastReturn.Value.Request);
                Check((await Report(sourceIds[2])).LifecycleRevision==returned.LifecycleRevision&&(await Report(target.Id)).PayloadRevision==targetBeforeReplay.PayloadRevision&&(await Intent()).State=="COMPLETED",
                    "replayed return cannot subtract twice or enqueue a second result");
                await Review(1,"return");await Materialize("return-second-approved",1,1);
                await Review(0,"return");await Materialize("return-last-approved",0,null);
                Check((await Instance()).Generation==generation,"all returns reuse saved report-set configuration without editor resave");
                // Native author submit must also invalidate REPORT_SET when the reviewer has
                // explicitly configured automatic approval. Only fresh fixture credentials.
                var autoSource=await Report(sourceIds[0]);
                using(var response=await http.PatchAsJsonAsync("work-assignments/"+autoSource.WorkAssignmentId+"/auto-approve-condition",
                    new { AutoApproveConditionJson="{\"enabled\":true,\"operator\":\"always\"}" },AggregateCanonical.Json,ct)){
                    var responseText=await response.Content.ReadAsStringAsync(ct);
                    if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Configure own auto approval: "+response.StatusCode+" "+responseText);
                }
                var author=await db.Users.Find(u=>u.Id==autoSource.AssigneeUserId).SingleAsync(ct);
                if(author.PasswordHash!=null||!author.Username.StartsWith(work.AutoCode+"-",StringComparison.Ordinal))
                    throw new InvalidOperationException("Auto approval author is not a fresh own fixture account.");
                var authorPassword=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
                var authorHash=new PasswordHasher<AppUser>().HashPassword(author,authorPassword);
                Check((await db.Users.UpdateOneAsync(u=>u.Id==author.Id&&u.PasswordHash==null,
                    Builders<AppUser>.Update.Set(u=>u.PasswordHash,authorHash),cancellationToken:ct)).ModifiedCount==1,"own source author credential CAS");
                var authorLogin=await Post("auth/login",new{username=author.Username,password=authorPassword});
                http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",authorLogin.GetProperty("accessToken").GetString());
                await Post("work-assignment-reports/"+autoSource.Id+"/submit",new tdtd_be.DTOs.WorkAssignmentReports.SubmitWorkAssignmentReportRequest{
                    CommandId=run+"-native-auto-submit",ExpectedPayloadRevision=autoSource.PayloadRevision,
                    ExpectedLifecycleRevision=autoSource.LifecycleRevision,LateReason="Dữ liệu thử riêng P05"});
                var autoApproved=await Report(autoSource.Id);
                Check(autoApproved.Status==WorkAssignmentReportStatus.Approved&&autoApproved.AutoApprovedAtUtc.HasValue
                    &&autoApproved.PayloadHash==autoSource.PayloadHash,"native submit automatically approves without altering source payload");
                var reviewerLogin=await Post("auth/login",new{username=actor.Username,password});
                http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",reviewerLogin.GetProperty("accessToken").GetString());
                await Materialize("native-auto-approved",1,1);
                if(Environment.GetEnvironmentVariable("AGG_REPORT_SET_METADATA")=="1"){
                    // Exercise native commands; no database mutation is used to simulate metadata events.
                    // Work/completion commands publish durable intents. Their delivery uses the
                    // existing periodic dispatcher, disabled globally on this private host.
                    async Task DispatchMetadata(){
                        Check(await AggregateHostIntegration.DispatchAsync(db,runner,config,100,ct,f.WorkId)==1,
                            "existing dispatcher delivers native metadata intent for this own Work");
                    }
                    async Task ChangeWorkEnd(DateTime end){
                        var current=await db.Works.Find(w=>w.Id==f.WorkId).SingleAsync(ct);
                        var request=new tdtd_be.DTOs.Works.WorkUpdateRequest(null,null,null,null,null,null,current.StartDate??new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc),end,current.DueDate,null,null,
                            ExpectedUpdatedAtUtc:current.UpdatedAtUtc);
                        using var response=await http.PutAsJsonAsync("works/"+f.WorkId,request,AggregateCanonical.Json,ct);
                        var body=await response.Content.ReadAsStringAsync(ct);
                        if(!response.IsSuccessStatusCode){
                            var error=JsonSerializer.Deserialize<JsonElement>(body);
                            if(response.StatusCode!=HttpStatusCode.Conflict||error.GetProperty("errorCode").GetString()!="WORK_DATES_CONFIRM_REQUIRED")
                                throw new InvalidOperationException("Native Work end: "+response.StatusCode+" "+body);
                            var details=error.GetProperty("details");
                            var ownIds=(await db.WorkAssignments.Find(a=>a.WorkId==f.WorkId).ToListAsync(ct)).Select(a=>a.Id).ToHashSet();
                            Check(details.GetProperty("retainedReportPeriods").GetBoolean()&&details.GetProperty("affected").EnumerateArray().All(a=>ownIds.Contains(a.GetProperty("id").GetString()!)),
                                "deadline confirmation only affects own fixture assignments and retains periods");
                            using var confirmed=await http.PutAsJsonAsync("works/"+f.WorkId,request with{DeadlineConfirmationToken=details.GetProperty("confirmationToken").GetString()},AggregateCanonical.Json,ct);
                            var confirmedBody=await confirmed.Content.ReadAsStringAsync(ct);
                            if(!confirmed.IsSuccessStatusCode)throw new InvalidOperationException("Confirm own Work dates: "+confirmed.StatusCode+" "+confirmedBody);
                        }
                        await DispatchMetadata();
                    }
                    await ChangeWorkEnd(new DateTime(2026,12,31,0,0,0,DateTimeKind.Utc));
                    await Materialize("work-end-without-filter",1,1);
                    await Queue("work-end-filter",new(1,"AND",[new("WORK_END","GE",["2026-12-31"])]));
                    await Materialize("work-end-included",1,1);
                    await ChangeWorkEnd(new DateTime(2026,12,30,0,0,0,DateTimeKind.Utc));
                    await Materialize("work-end-excluded",0,null);
                    await ChangeWorkEnd(new DateTime(2026,12,31,0,0,0,DateTimeKind.Utc));
                    await Materialize("work-end-restored",1,1);
                    await Queue("assignment-completed-filter",new(1,"AND",[new("ASSIGNMENT_COMPLETED","PRESENT",[])]));
                    await Materialize("before-assignment-completion",0,null);
                    http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",authorLogin.GetProperty("accessToken").GetString());
                    var sourceAssignment=await db.WorkAssignments.Find(a=>a.Id==autoSource.WorkAssignmentId).SingleAsync(ct);
                    var requested=await Post("work-assignments/"+sourceAssignment.Id+"/completion/requests",
                        new tdtd_be.DTOs.WorkAssignments.CompletionRequestCommand(run+"-completion-request",sourceAssignment.CompletionRevision,"Thử metadata P05"));
                    var requestId=requested.GetProperty("history").EnumerateArray().Single(h=>h.GetProperty("state").GetString()=="PENDING").GetProperty("id").GetString();
                    http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",reviewerLogin.GetProperty("accessToken").GetString());
                    var decided=await Post("work-assignments/"+sourceAssignment.Id+"/completion/requests/"+requestId+"/decision",
                        new tdtd_be.DTOs.WorkAssignments.CompletionDecisionCommand(run+"-completion-decision",requested.GetProperty("revision").GetInt64(),true,"Thử metadata P05"));
                    Check(decided.GetProperty("completed").GetBoolean()&&!decided.GetProperty("projectionPending").GetBoolean(),"native completion accepted and projections converged");
                    await DispatchMetadata();
                    await Materialize("assignment-completed",1,1);
                    var completedAssignment=await db.WorkAssignments.Find(a=>a.Id==sourceAssignment.Id).SingleAsync(ct);
                    var completionDay=tdtd_be.Common.Time.WorkCompletionDate.FromUtc(completedAssignment.CompletedAtUtc!.Value);
                    Check(completedAssignment.CompletedDate==completionDay && completedAssignment.CompletedDate.Value.Kind==DateTimeKind.Utc,
                        "native completion persists canonical Vietnam civil day through BSON");
                    var dayText=completionDay.ToString("yyyy-MM-dd");
                    await Queue("assignment-completed-exact-day",new(1,"AND",[new("ASSIGNMENT_COMPLETED","EQ",[dayText])]));
                    await Materialize("assignment-completed-exact-day",1,1);
                    await Queue("assignment-completed-previous-day",new(1,"AND",[new("ASSIGNMENT_COMPLETED","EQ",[completionDay.AddDays(-1).ToString("yyyy-MM-dd")])]));
                    await Materialize("assignment-completed-previous-day",0,null);
                    await Queue("assignment-completed-day-range",new(1,"AND",[new("ASSIGNMENT_COMPLETED","RANGE",[dayText,dayText])]));
                    await Materialize("assignment-completed-day-range",1,1);
                    await Queue("assignment-completed-present",new(1,"AND",[new("ASSIGNMENT_COMPLETED","PRESENT",[])]));
                    await Materialize("assignment-completed-present-restored",1,1);
                    var reopened=await Post("work-assignments/"+sourceAssignment.Id+"/completion/reopen",
                        new tdtd_be.DTOs.WorkAssignments.CompletionReopenCommand(run+"-completion-reopen",decided.GetProperty("revision").GetInt64(),autoSource.WorkReportPeriodId!,"Thử metadata P05"));
                    Check(!reopened.GetProperty("completed").GetBoolean()&&!reopened.GetProperty("projectionPending").GetBoolean(),"native reopen clears completion and converges");
                    await DispatchMetadata();
                    await Materialize("assignment-reopened",0,null);
                    Check((await Report(autoSource.Id)).PayloadHash==autoSource.PayloadHash,"metadata commands preserve source payload");
                    await Queue("restore-no-filter");await Materialize("metadata-filter-removed",1,1);
                    generation=(await Instance()).Generation;
                }
                await Review(0,"return");await Materialize("return-auto-approved",0,null);
                Check((await Instance()).Generation==generation,"auto approval and assigning reviewer Return reuse configuration without editor resave");
            }else{
            for(var i=2;i<82;i++)await Review(i,"approve");await Materialize("burst-through-82",82,82*83/2);
            await Review(82,"approve");await Materialize("83rd-approved",83,83*84/2);
            for(var i=83;i<165;i++)await Review(i,"approve");await Materialize("burst-through-165",165,165*166/2);
            await Review(165,"approve");await Materialize("166th-approved",166,166*167/2);
            Check((await Instance()).Generation==generation,"all approvals reuse initial saved method/generation");
            await Review(165,"deactivate");await Materialize("hide-last",165,165*166/2);
            await Review(165,"reactivate");await Materialize("show-last",166,166*167/2);
            await Review(165,"recall-approved");await Materialize("recall-last",165,165*166/2);
            await Review(165,"return");await Materialize("return-last",165,165*166/2);
            await Queue("resave-full-source-set");await Materialize("resave-full-source-set",165,165*166/2);
            }
            var all=await db.WorkAssignmentReports.Find(r=>r.WorkId==f.WorkId&&!r.IsDeleted).ToListAsync(ct);
            Check(all.SelectMany(r=>r.LifecycleProjectionOutbox).All(e=>e.State==WorkReportLifecycleProjectionOutboxStates.Completed),"every native lifecycle outbox completed");
            await FixtureProvenanceChecks.Migration(db,"P05 approval arrivals after native lifecycle/readback",ct);outcome="PASS";
        }finally{
            JobStorage.Current=oldStorage;JobActivator.Current=oldActivator;
            if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"p05-arrival-host-"+run+".log"),(await stdout)+(await stderr),CancellationToken.None);
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{run,state=outcome,checks=local,f.WorkId,targetId=target.Id,sourceIds,configId,instanceId,samples,events,
                scope="NORMAL_NATIVE_APPROVE_HIDE_RECALL_RETURN_HTTP_PRIVATE_HANGFIRE_COUNT_REAL_WORKER_SOURCE_READS",
                returnFocus,limitation=returnFocus?"Three own sources initially Submitted; native Approved-to-Draft Return with unchanged source payload and unchanged configuration, idempotent replay, actual queued recomputation and content paging. No browser or SLA claim."
                    :"Source payloads and initial Submitted state seeded. Bursts 3..82 and 84..165 pause queue consumption, not approval/event delivery. No browser or SLA claim."},new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
            Console.WriteLine("ARRIVAL_EVIDENCE "+path);
        }
    }
    private sealed class PrivateActivator(IServiceProvider services):JobActivator{public override object ActivateJob(Type type)=>ActivatorUtilities.CreateInstance(services,type);}
    private sealed class CountedPayloadReader(IWorkReportPayloadReader inner,IReadOnlyList<string> sourceIds):IWorkReportPayloadReader
    {
        private readonly HashSet<string> sources=sourceIds.ToHashSet(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<string,int> Calls=new();
        public Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(WorkAssignmentReport report,CancellationToken ct=default){if(sources.Contains(report.Id))Calls.AddOrUpdate(report.Id,1,(_,n)=>n+1);return inner.LoadReportPayloadAsync(report,ct);}
        public Task<string?> LoadReportTableBlockAsync(WorkAssignmentReport report,string blockId,CancellationToken ct=default)=>inner.LoadReportTableBlockAsync(report,blockId,ct);
        public Task<IReadOnlyDictionary<string,string>> LoadReportTableBlocksAsync(WorkAssignmentReport report,IEnumerable<string> blockIds,CancellationToken ct=default)=>inner.LoadReportTableBlocksAsync(report,blockIds,ct);
    }
}
