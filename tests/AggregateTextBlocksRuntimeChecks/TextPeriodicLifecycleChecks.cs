using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class TextPeriodicLifecycleChecks
{
    internal static async Task AuditOwnCleanup(MongoDbContext db,string run,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(run,"^r8-text-20261005-[0-9a-f]{8}$"))throw new InvalidOperationException("Invalid own fixture run.");
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync("../outputs/aggregate-operators-20261005/p05-periodic-revisions-"+run+".json",ct));
        var workId=manifest.RootElement.GetProperty("workId").GetString()!;
        _=await db.Works.Find(w=>w.Id==workId&&w.AutoCode==run+"-monthly").SingleAsync(ct);
        var ids=manifest.RootElement.GetProperty("targetReportIds").EnumerateArray().Select(id=>id.GetString()!).ToArray();
        var reports=await db.WorkAssignmentReports.Find(r=>r.WorkId==workId&&ids.Contains(r.Id)&&!r.IsDeleted).ToListAsync(ct);
        if(reports.Count!=3||reports.Any(r=>r.Status!=WorkAssignmentReportStatus.Draft))throw new InvalidOperationException("Own cleanup inventory/status changed.");
        var lockIds=manifest.RootElement.GetProperty("sourceReportIds").EnumerateArray().Select(id=>"REPORT:"+id.GetString()).ToArray();
        var locks=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks).Find(new BsonDocument("_id",new BsonDocument("$in",new BsonArray(lockIds)))).ToListAsync(ct);
        if(locks.Any(row=>AggregateMongoTransaction.Read<AggregateLockState>(row).Value.Owners.Count!=0))throw new InvalidOperationException("Own cleanup retained source owners.");
        var pending=reports.SelectMany(r=>r.LifecycleProjectionOutbox).Where(entry=>entry.State!=WorkReportLifecycleProjectionOutboxStates.Completed).ToArray();
        var jobRows=await db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection).Find(new BsonDocument("workId",workId)).ToListAsync(ct);
        var evidence=new{run,mode="READ_ONLY_CLEANUP_AUDIT",workId,pendingCount=pending.Length,
            reports=reports.Select(r=>new{r.Id,status=r.Status.ToString(),r.PayloadRevision,r.PayloadHash,
                outbox=r.LifecycleProjectionOutbox.Select(e=>new{e.Operation,e.LifecycleRevision,e.State,e.AttemptCount,e.LastError,e.DirectProjectionState,e.DirectProjectionReason})}),
            locks=locks.Select(row=>row.ToJson()),jobs=jobRows.Select(row=>new{id=row["_id"].AsString,state=row["state"].AsString,
                error=row.GetValue("error",BsonNull.Value).ToString(),hasResult=row.Contains("result"),hasProgress=row.Contains("progress"),attempt=row.GetValue("attempt",0).ToInt32()})};
        await File.WriteAllTextAsync("../outputs/aggregate-operators-20261005/p05-periodic-lifecycle-cleanup-audit-"+run+".json",JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}),ct);
        if(pending.Length!=0)throw new InvalidOperationException("Own fixture has lifecycle projection entries not completed; see audit.");
        Console.WriteLine("PASS own cleanup read-only audit: all lifecycle outbox entries completed; work="+workId);
    }
    internal static async Task CleanupOwn(MongoDbContext db,string run,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(run,"^r8-text-20261005-[0-9a-f]{8}$"))throw new InvalidOperationException("Invalid own fixture run.");
        using var backup=JsonDocument.Parse(await File.ReadAllTextAsync("../outputs/aggregate-operators-20261005/p05-periodic-lifecycle-backup-"+run+".json",ct));
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync("../outputs/aggregate-operators-20261005/p05-periodic-revisions-"+run+".json",ct));
        var workId=manifest.RootElement.GetProperty("workId").GetString()!;
        if(backup.RootElement.GetProperty("workId").GetString()!=workId)throw new InvalidOperationException("Fixture backup differs from manifest.");
        var work=await db.Works.Find(w=>w.Id==workId&&w.AutoCode==run+"-monthly").SingleAsync(ct);
        async Task<WorkAssignmentReport> Load(string id)=>await db.WorkAssignmentReports.Find(r=>r.Id==id&&r.WorkId==workId&&!r.IsDeleted).SingleAsync(ct);
        var targets=new List<WorkAssignmentReport>();var sources=new List<WorkAssignmentReport>();
        foreach(var id in manifest.RootElement.GetProperty("targetReportIds").EnumerateArray())targets.Add(await Load(id.GetString()!));
        foreach(var id in manifest.RootElement.GetProperty("sourceReportIds").EnumerateArray())sources.Add(await Load(id.GetString()!));
        var root=await db.WorkAssignments.Find(a=>a.Id==targets[0].WorkAssignmentId&&a.WorkId==workId).SingleAsync(ct);
        var binding=await db.WorkTemplateAssignees.Find(b=>b.WorkAssignmentId==root.Id&&b.WorkId==workId).SingleAsync(ct);
        var targetForm=await db.DynamicFormTemplates.Find(t=>t.Id==binding.DynamicFormTemplateId).SingleAsync(ct);
        var sourceForm=await db.DynamicFormTemplates.Find(t=>t.Id==sources[0].DynamicFormTemplateId).SingleAsync(ct);
        var fixture=new RuntimeFixture(work.CreatedByUserId??throw new InvalidOperationException("Fixture author missing."),
            root.CurrentReviewerUserId??throw new InvalidOperationException("Fixture reviewer missing."),workId,binding,targets[0],sources[0],targetForm,sourceForm);
        await Run(db,null,new WorkReportPayloadService(db),fixture,run,manifest.RootElement.GetProperty("configId").GetString()!,
            manifest.RootElement.GetProperty("instanceIds").EnumerateArray().Select(v=>v.GetString()!).ToArray(),targets,sources,
            (ok,label)=>{if(!ok)throw new InvalidOperationException(label);Console.WriteLine("PASS cleanup "+label);},ct,cleanupOnly:true);
    }
    internal static async Task Run(MongoDbContext db,AggregateMongoStore? store,WorkReportPayloadService payloads,
        RuntimeFixture f,string run,string configId,IReadOnlyList<string> instanceIds,
        IReadOnlyList<WorkAssignmentReport> targets,IReadOnlyList<WorkAssignmentReport> sources,
        Action<bool,string> check,CancellationToken ct,bool cleanupOnly=false)
    {
        var work=await db.Works.Find(w=>w.Id==f.WorkId&&!w.IsDeleted).SingleAsync(ct);
        if(work.AutoCode!=run+"-monthly"||!work.AutoCode.StartsWith("r8-text-",StringComparison.Ordinal)
            ||targets.Count!=3||sources.Count!=3||targets.Any(r=>r.WorkId!=f.WorkId)||sources.Any(r=>r.WorkId!=f.WorkId))
            throw new InvalidOperationException("Lifecycle checks require a fresh own monthly fixture.");
        var output="../outputs/aggregate-operators-20261005/";
        // Optional credential for browser follow-up on newly created synthetic accounts only.
        // Cleanup must never reuse an externally supplied credential for existing accounts.
        var browserPassword=cleanupOnly?null:Environment.GetEnvironmentVariable("AGG_FIXTURE_PASSWORD");
        if(browserPassword is not null&&browserPassword.Length<16)
            throw new InvalidOperationException("Synthetic browser credential must contain at least 16 characters.");
        var password=browserPassword??Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var actorIds=new[]{f.Actor,f.Outsider,sources[0].AssigneeUserId}.Distinct().ToArray();
        var actors=await db.Users.Find(u=>actorIds.Contains(u.Id)&&!u.IsDeleted).ToListAsync(ct);
        if(actors.Count!=actorIds.Length||actors.Any(u=>!cleanupOnly&&u.PasswordHash!=null||!u.Username.StartsWith(work.AutoCode+"-",StringComparison.Ordinal)))
            throw new InvalidOperationException("Only fresh synthetic accounts may receive test credentials.");
        var root=await db.WorkAssignments.Find(a=>a.Id==targets[0].WorkAssignmentId&&a.WorkId==f.WorkId).SingleAsync(ct);
        await File.WriteAllTextAsync(output+"p05-periodic-lifecycle-"+(cleanupOnly?"cleanup-backup-":"backup-")+run+".json",
            JsonSerializer.Serialize(new{run,workId=f.WorkId,assignment=root.ToJson(),users=actors.Select(u=>u.ToJson()),
                reports=targets.Concat(sources).Select(r=>r.ToJson())},new JsonSerializerOptions{WriteIndented=true}),ct);
        using(var session=await db.Db.Client.StartSessionAsync(cancellationToken:ct)){
            session.StartTransaction(new TransactionOptions(writeConcern:WriteConcern.WMajority));
            try{
                foreach(var user in actors){
                    var username=user.Username;var previousHash=user.PasswordHash;user.Username=username.ToLowerInvariant();user.FullName="Kiểm thử P05 định kỳ — dữ liệu giả";
                    user.PasswordHash=new PasswordHasher<AppUser>().HashPassword(user,password);
                    var result=await db.Users.ReplaceOneAsync(session,u=>u.Id==user.Id&&u.Username==username&&u.PasswordHash==previousHash,user,cancellationToken:ct);
                    if(result.ModifiedCount!=1)throw new InvalidOperationException("Fixture account changed concurrently.");
                }
                var changed=cleanupOnly?null:await db.WorkAssignments.UpdateOneAsync(session,a=>a.Id==root.Id&&a.WorkId==f.WorkId&&a.CreatedByUserId==f.Actor,
                    Builders<WorkAssignment>.Update.Set(a=>a.CreatedByUserId,f.Outsider).Set(a=>a.CurrentReviewerUserId,f.Outsider),cancellationToken:ct);
                if(!cleanupOnly&&changed!.ModifiedCount!=1||cleanupOnly&&root.CurrentReviewerUserId!=f.Outsider)throw new InvalidOperationException("Fixture reviewer changed concurrently.");
                await session.CommitTransactionAsync(ct);
            }catch{await session.AbortTransactionAsync(CancellationToken.None);throw;}
        }
        var projection=new DocRoleReadModelProjectionService(db);var roles=new DocRoleService(db,projection);
        await roles.UpsertWorkRootRolesAsync(work,ct);
        foreach(var assignment in await db.WorkAssignments.Find(a=>a.WorkId==f.WorkId&&!a.IsDeleted).ToListAsync(ct))
            await roles.UpsertWorkAssignmentRolesAsync(assignment,ct);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(f.WorkId,f.Actor,ct);
        await projection.RebuildWorkAssignmentsAsync(f.WorkId,f.Actor,ct);
        await projection.RebuildWorkReportPeriodsAsync(f.WorkId,f.Actor,ct);
        await FixtureProvenanceChecks.Migration(db,"P05 monthly lifecycle before full host",ct);

        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,
            CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        foreach(var pair in new Dictionary<string,string>{
            ["ASPNETCORE_ENVIRONMENT"]="Development",["DOTNET_ENVIRONMENT"]="Development",["ASPNETCORE_URLS"]="http://127.0.0.1:"+port,
            ["Mongo__ConnectionString"]="mongodb://localhost:27017/?replicaSet=tdtd-rs",["Mongo__Database"]="tdtd",
            ["Hangfire__ServerEnabled"]="false",["Hangfire__DashboardEnabled"]="false",["Hangfire__RecurringRegistrationEnabled"]="false",
            ["Hangfire__Prefix"]="p05_lifecycle_"+run.Replace('-','_'),["Redis__Enabled"]="false",["Frontend__Enabled"]="false",
            ["Jwt__Issuer"]=run,["Jwt__Audience"]=run,["Jwt__Key"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["AggregateMapping__V2Enabled"]="true",["AggregateMapping__ConfirmationKeyBase64"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))})
            start.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(start)??throw new InvalidOperationException("P05 full host failed to start.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri("http://127.0.0.1:"+port+"/api/"),Timeout=TimeSpan.FromSeconds(40)};
        var localChecks=0;var events=new List<object>();
        void Check(bool value,string label){check(value,"P05 full monthly "+label);localChecks++;}
        async Task<(HttpStatusCode Status,JsonElement Body)> Send(string path,object body){
            using var response=await http.PostAsJsonAsync(path,body,AggregateCanonical.Json,ct);
            var raw=await response.Content.ReadAsStringAsync(ct);
            var parsed=string.IsNullOrEmpty(raw)?default:JsonSerializer.Deserialize<JsonElement>(raw);
            if((int)response.StatusCode>=500)throw new InvalidOperationException(path+" returned "+(int)response.StatusCode+": "+raw);
            if(parsed.ValueKind==JsonValueKind.Object&&parsed.TryGetProperty("lifecycleProjectionPending",out var pending)&&pending.ValueKind==JsonValueKind.True)
                throw new InvalidOperationException(path+" committed with projection pending; inspect private full-host log.");
            return(response.StatusCode,parsed);
        }
        async Task<JsonElement> Ok(string path,object body){var result=await Send(path,body);if(result.Status is not(HttpStatusCode.OK or HttpStatusCode.Accepted))
            throw new InvalidOperationException(path+" returned "+(int)result.Status+": "+result.Body);return result.Body;}
        async Task Login(string id){http.DefaultRequestHeaders.Authorization=null;var user=actors.Single(u=>u.Id==id);
            var result=await Send("auth/login",new{username=user.Username,password});
            if(result.Status!=HttpStatusCode.OK)throw new InvalidOperationException("Normal login failed for synthetic account: "+(int)result.Status);
            http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",result.Body.GetProperty("accessToken").GetString());}
        Task<WorkAssignmentReport> Report(int i)=>db.WorkAssignmentReports.Find(r=>r.Id==targets[i].Id).SingleAsync(ct);
        Task<WorkAssignmentReport> Source(int i)=>db.WorkAssignmentReports.Find(r=>r.Id==sources[i].Id).SingleAsync(ct);
        async Task<AggregateInstanceState> Instance(int i)=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
            .Find(new BsonDocument("_id",instanceIds[i])).SingleAsync(ct)).Value;
        async Task<AggregateLockState?> Lock(int source)=> (await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks)
            .Find(new BsonDocument("_id","REPORT:"+sources[source].Id)).FirstOrDefaultAsync(ct)) is {} row?AggregateMongoTransaction.Read<AggregateLockState>(row).Value:null;
        async Task<string> Frozen(int target)=>string.Join("\n",(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen)
            .Find(new BsonDocument("target",targets[target].Id)).Sort(new BsonDocument("_id",1)).ToListAsync(ct)).Select(r=>r.ToJson()));
        async Task<AggregatePeriodContextDto> Context(int i)=>(await Ok("aggregate-v2/editor/bootstrap",new{reportId=targets[i].Id}))
            .GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        async Task<string> Submission(int i)=>(await Ok("aggregate-v2/reports/"+targets[i].Id+"/submission-preview",new AggregateInstanceReadCommandDto(await Context(i))))
            .GetProperty("token").GetString()!;
        async Task<SubmitWorkAssignmentReportRequest> SubmitRequest(int i,string id,string? token){var report=await Report(i);return new(){CommandId=run+id,
            ExpectedPayloadRevision=report.PayloadRevision,ExpectedLifecycleRevision=report.LifecycleRevision,AggregateConfirmationToken=token,LateReason="Fixture P05",Note="Dữ liệu giả P05"};}
        async Task Review(int i,string operation){var report=await Report(i);object request=operation=="approve"?new ApproveReportRequest{CommandId=run+"-"+i+"-"+operation,
            ExpectedPayloadRevision=report.PayloadRevision,ExpectedLifecycleRevision=report.LifecycleRevision,Comment="Kiểm thử P05"}:new ReturnReportRequest{CommandId=run+"-"+i+"-"+operation,
            ExpectedPayloadRevision=report.PayloadRevision,ExpectedLifecycleRevision=report.LifecycleRevision,Comment="Kiểm thử P05"};
            await Ok("work-assignment-review/reports/"+report.Id+"/"+operation,request);events.Add(new{report.Id,operation});}
        try{
            var ready=false;
            for(var attempt=0;attempt<120&&!process.HasExited;attempt++){
                try{using var ping=await http.GetAsync("../health/ready",ct);if(ping.IsSuccessStatusCode){ready=true;break;}}
                catch(HttpRequestException){}await Task.Delay(500,ct);
            }
            if(!ready)throw new InvalidOperationException("Normal Development full host not ready; inspect private logs.");
            Console.WriteLine("OWN_FULLHOST_READY port="+port+" work="+f.WorkId+" environment=Development normal migrations; shared BE unchanged.");
            if(cleanupOnly){
                await Login(f.Outsider);
                async Task CleanupCall(int i,string operation){
                    var report=await Report(i);var request=new ReturnReportRequest{CommandId=run+"-failure-cleanup-"+i+"-"+operation,
                        ExpectedPayloadRevision=report.PayloadRevision,ExpectedLifecycleRevision=report.LifecycleRevision,Comment="Dọn trạng thái fixture kiểm lỗi P05"};
                    using var response=await http.PostAsJsonAsync("work-assignment-review/reports/"+report.Id+"/"+operation,request,AggregateCanonical.Json,ct);
                    var raw=await response.Content.ReadAsStringAsync(ct);
                    if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new InvalidOperationException("Fixture cleanup "+operation+" rejected: "+(int)response.StatusCode+" "+raw);
                    Console.WriteLine("OWN_FAILURE_CLEANUP report="+report.Id+" operation="+operation+" nativeStatus="+(await Report(i)).Status);
                }
                for(var i=0;i<targets.Count;i++){
                    if((await Report(i)).Status==WorkAssignmentReportStatus.Approved)await CleanupCall(i,"recall-approved");
                    if((await Report(i)).Status==WorkAssignmentReportStatus.Submitted)await CleanupCall(i,"return");
                    if((await Report(i)).Status!=WorkAssignmentReportStatus.Draft)throw new InvalidOperationException("Fixture cleanup did not reach Draft.");
                }
                if((await Lock(0))?.Owners.Count is >0||(await Lock(1))?.Owners.Count is >0)throw new InvalidOperationException("Own fixture owners remain after native cleanup.");
                await FixtureProvenanceChecks.Migration(db,"P05 monthly failure cleanup",ct);
                await File.WriteAllTextAsync(output+"p05-periodic-lifecycle-cleanup-"+run+".json",JsonSerializer.Serialize(new{run,state="CLEANUP_ONLY_NOT_TEST_PASS",workId=f.WorkId,
                    targetReportIds=targets.Select(r=>r.Id),targetStatuses=new[]{(await Report(0)).Status.ToString(),(await Report(1)).Status.ToString(),(await Report(2)).Status.ToString()},
                    sourceOwners=new[]{(await Lock(0))?.Owners.Count??0,(await Lock(1))?.Owners.Count??0}},new JsonSerializerOptions{WriteIndented=true}),ct);
                return;
            }
            await Login(f.Actor);
            var october=await Instance(1);var octoberContext=await Context(1);
            var r3=(await Ok("aggregate-v2/configs/"+configId+"/read",new AggregateConfigReadCommandDto(octoberContext,3)))
                .GetProperty("version").GetProperty("recipe").Deserialize<AggregateRecipeDto>(AggregateCanonical.Json)!;
            var cumulativeRule=r3.TimeRules.Single() with{Mode="CUMULATIVE_FROM",StartDate="2026-09-01",EndDate=null};
            var selection=new AggregateInstanceSelectionDto([new("s","FORM_SELECTOR",[],[])]);
            var change=new AggregateMappingChangeDto(october.Revision,new(AggregateCanonical.Hash(r3),[cumulativeRule],[],[]),selection,[],false);
            var prepared=await Ok("aggregate-v2/instances/"+instanceIds[1]+"/mapping/preview",new AggregateMappingPreviewCommandDto(octoberContext,change));
            await Ok("aggregate-v2/instances/"+instanceIds[1]+"/apply",new AggregateMappingApplyCommandDto(run+"-life-october-cumulative",octoberContext,change,prepared.GetProperty("token").GetString()!));
            Check((await payloads.LoadReportPayloadAsync(await Report(1),ct)).FieldValuesJson!.Contains("\"metric\":30",StringComparison.Ordinal),"October cumulative overlay native result30 before submission");
            var septBefore=await Report(0);
            var noToken=await Send("work-assignment-reports/"+targets[0].Id+"/submit",await SubmitRequest(0,"-missing-confirmation",null));
            Check((int)noToken.Status>=400&&noToken.Body.GetRawText().Contains("AGG_CONFIRMATION_REQUIRED",StringComparison.Ordinal)
                &&(await Report(0)).LifecycleRevision==septBefore.LifecycleRevision&&(await Report(0)).PayloadHash==septBefore.PayloadHash&&(await Instance(0)).State=="DRAFT",
                "Submit without confirmation leaves Draft/payload/lifecycle unchanged");
            var septSubmit=await SubmitRequest(0,"-life-sept-submit",await Submission(0));
            await Ok("work-assignment-reports/"+targets[0].Id+"/submit",septSubmit);
            var septSubmitted=await Report(0);var septFrozen=await Frozen(0);
            Check(septSubmitted.Status==WorkAssignmentReportStatus.Submitted&&(await Instance(0)).State=="FROZEN"&&(await Lock(0))?.Owners.Count==1,"Submit freezes September and owns source September");
            await Ok("work-assignment-reports/"+targets[0].Id+"/submit",septSubmit);
            Check((await Report(0)).LifecycleRevision==septSubmitted.LifecycleRevision&&await Frozen(0)==septFrozen&&(await Lock(0))?.Owners.Count==1,"Submit replay does not duplicate lifecycle/snapshot/owner");
            var octFirstToken=await Submission(1);
            await Ok("work-assignment-reports/"+targets[1].Id+"/submit",await SubmitRequest(1,"-life-oct-submit",octFirstToken));
            var octSubmitted=await Report(1);var octFrozen=await Frozen(1);
            Check(octSubmitted.Status==WorkAssignmentReportStatus.Submitted&&(await Instance(1)).State=="FROZEN"&&(await Lock(0))?.Owners.Count==2&&(await Lock(1))?.Owners.Count==1,"two submitted periods hold distinct owners of shared September source");
            var sourceBefore=await Source(0);
            var sourceRecall=new ReturnReportRequest{CommandId=run+"-source-recall-locked",ExpectedPayloadRevision=sourceBefore.PayloadRevision,
                ExpectedLifecycleRevision=sourceBefore.LifecycleRevision,Comment="Kiểm khóa hai kỳ"};
            var blocked=await Send("work-assignment-review/reports/"+sources[0].Id+"/recall-approved",sourceRecall);
            Check((int)blocked.Status>=400&&blocked.Body.GetRawText().Contains("AGG_SOURCE_LOCKED",StringComparison.Ordinal)&&(await Source(0)).LifecycleRevision==sourceBefore.LifecycleRevision,"source recall blocked while two upstream owners remain");
            await Login(f.Outsider);await Review(0,"approve");
            Check((await Report(0)).Status==WorkAssignmentReportStatus.Approved&&await Frozen(0)==septFrozen&&(await Lock(0))?.Owners.Count==2,"reviewer Approve preserves frozen snapshot and shared owners");
            await Login(f.Actor);
            var frozenWrite=await Send("aggregate-v2/instances/"+instanceIds[0]+"/mapping/preview",new AggregateMappingPreviewCommandDto(await Context(0),new((await Instance(0)).Revision,null,selection,[],false)));
            Check((int)frozenWrite.Status>=400&&(await Report(0)).PayloadHash==septSubmitted.PayloadHash&&await Frozen(0)==septFrozen,"submitted/approved mapping refuses changes and preserves payload/snapshot");
            var novemberContext=await Context(2);var r4=r3 with{TimeRules=[cumulativeRule]};
            var rejectedImpact=new AggregateConfigImpactRequestDto(3,r4,[new(instanceIds[0],(await Instance(0)).Revision)],[]);
            var frozenMigration=await Send("aggregate-v2/configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(novemberContext,rejectedImpact));
            Check((int)frozenMigration.Status>=400&&await Frozen(0)==septFrozen,"common revision cannot select a frozen period for migration");
            var impact=new AggregateConfigImpactRequestDto(3,r4,[new(instanceIds[2],(await Instance(2)).Revision)],[]);
            var impactPreview=await Ok("aggregate-v2/configs/"+configId+"/impact-preview",new AggregateConfigImpactCommandDto(novemberContext,impact));
            await Ok("aggregate-v2/configs/"+configId+"/revisions",new AggregateConfigRevisionCommandDto(run+"-life-common-r4",novemberContext,impact,impactPreview.GetProperty("token").GetString()!));
            Check((await Instance(2)).ConfigRevision==4&&(await Instance(0)).ConfigRevision==2&&(await Instance(1)).ConfigRevision==3
                &&(await Report(0)).PayloadHash==septSubmitted.PayloadHash&&(await Report(1)).PayloadHash==octSubmitted.PayloadHash&&await Frozen(0)==septFrozen&&await Frozen(1)==octFrozen,
                "r4 migrates only November draft; submitted September/October keep pins/payloads/frozen snapshots");
            await Login(f.Outsider);await Review(0,"recall-approved");
            Check((await Report(0)).Status==WorkAssignmentReportStatus.Submitted&&(await Instance(0)).State=="FROZEN"&&await Frozen(0)==septFrozen&&(await Lock(0))?.Owners.Count==2,"Recall approval leaves report submitted/frozen and owners intact");
            await Review(0,"return");
            Check((await Report(0)).Status==WorkAssignmentReportStatus.Draft&&(await Instance(0)).State=="DRAFT"&&(await Lock(0))?.Owners.Count==1
                &&(await Lock(0))!.Owners.Single().TargetReportId==targets[1].Id&&await Frozen(0)==septFrozen,"Return September releases only its owner; October still locks shared source");
            await Login(f.Actor);blocked=await Send("work-assignment-review/reports/"+sources[0].Id+"/recall-approved",sourceRecall);
            Check((int)blocked.Status>=400&&blocked.Body.GetRawText().Contains("AGG_SOURCE_LOCKED",StringComparison.Ordinal)&&(await Source(0)).Status==WorkAssignmentReportStatus.Approved,"source remains locked after only one parent period returns");
            await Login(f.Outsider);await Review(1,"return");
            Check((await Report(1)).Status==WorkAssignmentReportStatus.Draft&&(await Instance(1)).State=="DRAFT"&&(await Lock(0))?.Owners.Count==0&&(await Lock(1))?.Owners.Count==0
                &&await Frozen(0)==septFrozen&&await Frozen(1)==octFrozen,"last owner Return unlocks sources and preserves both historical snapshots");
            Check((await Source(0)).Status==WorkAssignmentReportStatus.Approved&&(await Source(1)).Status==WorkAssignmentReportStatus.Approved,"parent Returns do not cascade child report status");
            var refresh=new AggregateRefreshService(store??throw new InvalidOperationException("Runtime store is required."),new AggregateMongoCommandReader(db,payloads,true));
            for(var i=0;i<2;i++){
                var before=await Report(i);var state=await Instance(i);var result=await refresh.RunAsync(state.Id,state.Generation,ct);
                Check(result=="COMPLETED"&&(await Report(i)).PayloadRevision==before.PayloadRevision+1&&(await Instance(i)).ConfigRevision==(i==0?2:3),"Return refresh uses pinned config through native writer for period "+i);
                Check(await refresh.RunAsync(state.Id,state.Generation,ct)=="NO_WORK"&&(await Report(i)).PayloadRevision==before.PayloadRevision+1,"Return refresh retry does not write twice for period "+i);
            }
            Check(await Frozen(0)==septFrozen&&await Frozen(1)==octFrozen,"Draft refresh preserves frozen historical evidence");
            await Login(f.Actor);
            var staleSubmit=await Send("work-assignment-reports/"+targets[1].Id+"/submit",await SubmitRequest(1,"-life-stale-token",octFirstToken));
            Check((int)staleSubmit.Status>=400&&staleSubmit.Body.GetRawText().Contains("AGG_CONFIRMATION_STALE",StringComparison.Ordinal)&&(await Report(1)).Status==WorkAssignmentReportStatus.Draft&&(await Lock(0))?.Owners.Count==0,"old pre-Return confirmation cannot resubmit after lifecycle changes");
            await Ok("work-assignment-reports/"+targets[1].Id+"/submit",await SubmitRequest(1,"-life-oct-resubmit",await Submission(1)));
            var resubmitted=await Report(1);
            var historical=await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(new BsonDocument("target",targets[1].Id)).ToListAsync(ct);
            Check(resubmitted.Status==WorkAssignmentReportStatus.Submitted&&historical.Count==2&&historical.Any(row=>row.ToJson()==octFrozen)&&(await Lock(0))?.Owners.Count==1,"fresh resubmit creates a new frozen submission and preserves old history");
            await Ok("work-assignment-reports/"+targets[1].Id+"/withdraw-submitted",new ReturnWorkAssignmentReportRequest{CommandId=run+"-life-oct-withdraw",
                ExpectedPayloadRevision=resubmitted.PayloadRevision,ExpectedLifecycleRevision=resubmitted.LifecycleRevision,ReturnReason="Fixture kết thúc kiểm"});
            Check((await Report(1)).Status==WorkAssignmentReportStatus.Draft&&(await Lock(0))?.Owners.Count==0&&(await Lock(1))?.Owners.Count==0,"reporter Withdraw releases only the resubmitted period's source owners");

            // One source period now runs through native author/reviewer APIs, rather than remaining only seeded Approved.
            sourceBefore=await Source(0);
            await Ok("work-assignment-review/reports/"+sources[0].Id+"/recall-approved",new ReturnReportRequest{CommandId=run+"-life-source-recall",
                ExpectedPayloadRevision=sourceBefore.PayloadRevision,ExpectedLifecycleRevision=sourceBefore.LifecycleRevision,Comment="Fixture nguồn"});
            var sourceSubmitted=await Source(0);
            await Ok("work-assignment-review/reports/"+sources[0].Id+"/return",new ReturnReportRequest{CommandId=run+"-life-source-return",
                ExpectedPayloadRevision=sourceSubmitted.PayloadRevision,ExpectedLifecycleRevision=sourceSubmitted.LifecycleRevision,Comment="Fixture nguồn"});
            var sourceDraft=await Source(0);
            Check(sourceDraft.Status==WorkAssignmentReportStatus.Draft&&sourceDraft.PayloadHash==sourceBefore.PayloadHash,"unlocked source Recall/Return reaches Draft without altering native payload");
            await Login(sourceDraft.AssigneeUserId);
            await Ok("work-assignment-reports/"+sourceDraft.Id+"/submit",new SubmitWorkAssignmentReportRequest{CommandId=run+"-life-source-submit",
                ExpectedPayloadRevision=sourceDraft.PayloadRevision,ExpectedLifecycleRevision=sourceDraft.LifecycleRevision,LateReason="Fixture P05"});
            sourceSubmitted=await Source(0);
            Check(sourceSubmitted.Status==WorkAssignmentReportStatus.Submitted,"source author submits September through normal report API");
            await Login(f.Actor);
            await Ok("work-assignment-review/reports/"+sources[0].Id+"/approve",new ApproveReportRequest{CommandId=run+"-life-source-approve",
                ExpectedPayloadRevision=sourceSubmitted.PayloadRevision,ExpectedLifecycleRevision=sourceSubmitted.LifecycleRevision,Comment="Fixture P05"});
            Check((await Source(0)).Status==WorkAssignmentReportStatus.Approved&&(await Source(0)).PayloadHash==sourceBefore.PayloadHash,"source reviewer approves September through normal review API");
            var finalReports=await db.WorkAssignmentReports.Find(r=>r.WorkId==f.WorkId&&!r.IsDeleted).ToListAsync(ct);
            Check(finalReports.SelectMany(r=>r.LifecycleProjectionOutbox).All(e=>e.State==WorkReportLifecycleProjectionOutboxStates.Completed),
                "native lifecycle outbox completes for every own source/target transition");
            await Login(f.Actor);
            await TextPeriodicSchemaChecks.Run(db,f,run,configId,instanceIds,targets,sources,http,check,ct);
            var jobConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
                ["AggregateMapping:V2Enabled"]="true",["AggregateMapping:ConfirmationKeyBase64"]=start.Environment["AggregateMapping__ConfirmationKeyBase64"]}).Build();
            await TextPreviewJobBoundaryChecks.Run(db,run,http,jobConfig,targets[0].Id,sources[0].Id,instanceIds[0],check,ct);
            await TextAsyncComputationChecks.Run(db,run,http,jobConfig,targets[0].Id,instanceIds[0],check,ct);
            await TextSourceLifecycleChecks.Run(db,run,http,start.Environment["Hangfire__Prefix"]!,instanceIds[0],targets[0].Id,sources[0].Id,check,ct);
            if(Environment.GetEnvironmentVariable("AGG_P05_MATERIALIZATION_RECOVERY")=="1")
                await TextMaterializationRecoveryChecks.Run(db,run,http,jobConfig,instanceIds[0],targets[0].Id,sources[0].Id,check,ct);
            if(Environment.GetEnvironmentVariable("AGG_P05_OUTAGE_CHECKS")=="1")
                await TextMaterializationOutageChecks.Run(db,run,http,jobConfig,instanceIds.Take(2).ToArray(),targets.Take(2).Select(r=>r.Id).ToArray(),check,ct);
            await FixtureProvenanceChecks.Migration(db,"P05 monthly after native lifecycle",ct);
            await File.WriteAllTextAsync(output+"p05-periodic-lifecycle-"+run+".json",JsonSerializer.Serialize(new{run,state="PASS",checks=localChecks,workId=f.WorkId,
                targetReportIds=targets.Select(r=>r.Id),sourceReportIds=sources.Select(r=>r.Id),configId,
                headRevision=AggregateMongoTransaction.Read<AggregateConfigHead>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Configs).Find(new BsonDocument("_id",configId)).SingleAsync(ct)).Value.HeadRevision,instanceIds,
                finalPinnedConfigRevisions=new[]{(await Instance(0)).ConfigRevision,(await Instance(1)).ConfigRevision,(await Instance(2)).ConfigRevision},events,
                scope="NORMAL_DEVELOPMENT_PROGRAM_LOGIN_SUBMIT_REVIEW_RETURN_WITHDRAW_REAL_HTTP_NO_BROWSER_UAT"},new JsonSerializerOptions{WriteIndented=true}),ct);
        }finally{
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync(CancellationToken.None);
            await File.WriteAllTextAsync(output+"p05-periodic-lifecycle-"+(cleanupOnly?"cleanup-host-":"fullhost-")+run+".stdout.log",await stdout,CancellationToken.None);
            await File.WriteAllTextAsync(output+"p05-periodic-lifecycle-"+(cleanupOnly?"cleanup-host-":"fullhost-")+run+".stderr.log",await stderr,CancellationToken.None);
        }
    }
}
