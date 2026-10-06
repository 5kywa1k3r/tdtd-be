using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.AggregateMapping;

// Full application DI on a private Hangfire prefix. Never run the global assignment sweep.
internal static class TextCompletionRecoveryChecks
{
    internal static async Task Audit(MongoDbContext db,string run,CancellationToken ct){
        if(!System.Text.RegularExpressions.Regex.IsMatch(run,"^completion-recovery-[a-f0-9]{8}$"))throw new ArgumentException("Own run only.");
        var manifest=JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,run+".json"),ct));
        var assignmentId=manifest.GetProperty("autoAssignmentId").GetString();var queueId=manifest.GetProperty("queueId").GetString();var materializeId=manifest.GetProperty("materializeId").GetString();
        var a=await db.WorkAssignments.Find(a=>a.Id==assignmentId).SingleAsync(ct);
        var q=await db.WorkAssignmentQueueItems.Find(q=>q.Id==queueId).SingleAsync(ct);
        var j=await db.WorkAssignmentMaterializeJobs.Find(j=>j.Id==materializeId).SingleAsync(ct);
        Console.WriteLine(JsonSerializer.Serialize(new{a.Id,a.CompletedAtUtc,a.CompletedDate,a.CompletedByUserId,a.CompletionMode,a.CompletionProjectionPending,a.CompletionRevision,
            queue=new{q.IsActive,q.UpdatedByUserId},materializer=new{j.IsActive,j.UpdatedByUserId}}));
        if(a.CompletedAtUtc is { } completedAt && a.CompletedDate is { } completedDate){
            // Read-only seam check: same raw CompletedDate assignment used by AggregateMongoPreviewReader.
            var expectedDay=DateOnly.FromDateTime(completedAt.AddHours(7)).ToString("yyyy-MM-dd");
            var storedDay=DateOnly.FromDateTime(completedDate).ToString("yyyy-MM-dd");
            var effective=tdtd_be.Common.Time.WorkCompletionDate.Read(completedDate,a.CompletedAtUtc,a.CompletionMode);
            var header=new AggregateSourceHeader(null!,"",null!,"","",true,true,false,null){AssignmentCompletedDate=effective};
            bool? Matches(string day)=>AggregateReportSetFilter.Matches(new(1,"AND",[new("ASSIGNMENT_COMPLETED","EQ",[day])]),header,null!);
            Console.WriteLine("DATE_FILTER_SEAM "+JsonSerializer.Serialize(new{expectedDay,storedDay,expectedDayMatches=Matches(expectedDay),storedDayMatches=Matches(storedDay),scope="MONGO_VALUE_AND_REAL_FILTER_NOT_FULL_API"}));
            if(Matches(expectedDay)!=true || storedDay!=expectedDay && Matches(storedDay)!=false)
                throw new InvalidOperationException("Completion date compatibility filter mismatch.");
        }
        var prefix=run.Replace('-','_');var names=await(await db.Db.ListCollectionNamesAsync(cancellationToken:ct)).ToListAsync(ct);
        foreach(var name in names.Where(n=>n.StartsWith(prefix,StringComparison.Ordinal)&&!n.Contains("lock"))) {
            var rows=await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Limit(15).ToListAsync(ct);
            if(rows.Count>0)Console.WriteLine(name+" "+new BsonArray(rows).ToJson());
        }
    }
    internal static async Task Run(MongoDbContext db,CancellationToken ct)
    {
        const string workId="6ac4b7d0b143bc90e357d524", assignmentId="6ac4b7d0b143bc90e357d526";
        var work=await db.Works.Find(w=>w.Id==workId&&w.AutoCode=="r8-text-20261005-ff132fb1-scale").SingleAsync(ct);
        var before=await db.WorkAssignments.Find(a=>a.Id==assignmentId&&a.WorkId==work.Id&&!a.IsDeleted).SingleAsync(ct);
        if(!before.IsActive||!string.IsNullOrEmpty(before.FlowInstanceId)||!before.CompletedAtUtc.HasValue||before.CompletedByUserId!=work.CreatedByUserId)
            throw new InvalidOperationException("Not the known own committed completion fixture.");
        var run="completion-recovery-"+Guid.NewGuid().ToString("N")[..8];
        var evidence=Path.Combine(AppContext.BaseDirectory,run+".json");var checks=0;var state="FAILED";var jobIds=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks++;Console.WriteLine("PASS completion recovery "+label);}
        var requests=db.Db.GetCollection<WorkAssignmentCompletionRequest>("work_assignment_completion_requests");
        async Task<string> History(string id)=>JsonSerializer.Serialize((await requests.Find(r=>r.AssignmentId==id).ToListAsync(ct)).OrderBy(r=>r.Id));
        var historyBefore=await History(assignmentId);
        var reportsBefore=await db.WorkAssignmentReports.Find(r=>r.WorkId==work.Id).ToListAsync(ct);
        var periodsBefore=await db.WorkReportPeriods.Find(p=>p.WorkId==work.Id).ToListAsync(ct);
        var queueBefore=await db.WorkAssignmentQueueItems.Find(q=>q.WorkAssignmentId==assignmentId).ToListAsync(ct);
        var jobsBefore=await db.WorkAssignmentMaterializeJobs.Find(j=>j.WorkAssignmentId==assignmentId).ToListAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,run+"-before.json"),new BsonDocument{
            {"assignment",before.ToBsonDocument()},{"requests",BsonDocument.Parse("{rows:"+historyBefore+"}")},
            {"reports",new BsonArray(reportsBefore.Select(r=>r.ToBsonDocument()))},{"periods",new BsonArray(periodsBefore.Select(p=>p.ToBsonDocument()))},
            {"queue",new BsonArray(queueBefore.Select(q=>q.ToBsonDocument()))},{"jobs",new BsonArray(jobsBefore.Select(j=>j.ToBsonDocument()))}
        }.ToJson(new(){Indent=true}),ct);

        // Separate auto-completion fixture with one real pinned Approved report and a past due occurrence.
        var auto=await RuntimeFixture.Seed(db,run,ct);
        var due=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);var periodKey=due.ToString("yyyyMMdd");
        var autoBinding=await db.WorkTemplateAssignees.Find(b=>b.WorkAssignmentId==auto.Source.WorkAssignmentId).SingleAsync(ct);
        await db.WorkAssignments.UpdateOneAsync(a=>a.Id==auto.Source.WorkAssignmentId&&a.WorkId==auto.WorkId,
            Builders<WorkAssignment>.Update.Set(a=>a.StartDate,new DateTime(2026,9,1,0,0,0,DateTimeKind.Utc)).Set(a=>a.DueDate,due),cancellationToken:ct);
        await db.WorkTemplateAssignees.UpdateOneAsync(b=>b.Id==autoBinding.Id,Builders<WorkTemplateAssignee>.Update.Set(b=>b.DueDate,due),cancellationToken:ct);
        await db.WorkReportPeriods.UpdateOneAsync(p=>p.Id==auto.Source.WorkReportPeriodId,
            Builders<WorkReportPeriod>.Update.Set(p=>p.PeriodKey,periodKey).Set(p=>p.PeriodInstanceKey,autoBinding.Id+":"+periodKey).Set(p=>p.DueAtUtc,due),cancellationToken:ct);
        await db.WorkAssignmentReports.UpdateOneAsync(r=>r.Id==auto.Source.Id,
            Builders<WorkAssignmentReport>.Update.Set(r=>r.PeriodKey,periodKey).Set(r=>r.PeriodInstanceKey,autoBinding.Id+":"+periodKey).Set(r=>r.DueAtUtc,due),cancellationToken:ct);
        var queueId=ObjectId.GenerateNewId().ToString();var materializeId=ObjectId.GenerateNewId().ToString();
        await db.WorkAssignmentQueueItems.InsertOneAsync(new(){Id=queueId,WorkId=auto.WorkId,WorkAssignmentId=auto.Source.WorkAssignmentId,
            AssigneeUserId=auto.Source.AssigneeUserId,PeriodKey=periodKey,IsActive=true,CreatedByUserId=auto.Actor,UpdatedByUserId=auto.Actor},cancellationToken:ct);
        await db.WorkAssignmentMaterializeJobs.InsertOneAsync(new(){Id=materializeId,WorkId=auto.WorkId,WorkAssignmentId=auto.Source.WorkAssignmentId,
            IsActive=true,CreatedByUserId=auto.Actor,UpdatedByUserId=auto.Actor},cancellationToken:ct);
        await FixtureProvenanceChecks.Migration(db,"completion recovery before private normal host",ct);

        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var prefix=run.Replace('-','_');
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,CreateNoWindow=true,
            WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        foreach(var pair in new Dictionary<string,string>{["ASPNETCORE_ENVIRONMENT"]="Development",["DOTNET_ENVIRONMENT"]="Development",
            ["ASPNETCORE_URLS"]="http://127.0.0.1:"+port,["Mongo__ConnectionString"]="mongodb://localhost:27017/?replicaSet=tdtd-rs",["Mongo__Database"]="tdtd",
            ["Hangfire__ServerEnabled"]="true",["Hangfire__DashboardEnabled"]="false",["Hangfire__RecurringRegistrationEnabled"]="false",["Hangfire__Prefix"]=prefix,
            ["DOTNET_PROCESSOR_COUNT"]="2",["Redis__Enabled"]="false",["Frontend__Enabled"]="false",["AggregateMapping__V2Enabled"]="true",
            ["Jwt__Issuer"]=run,["Jwt__Audience"]=run,["Jwt__Key"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["AggregateMapping__ConfirmationKeyBase64"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))})start.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(start)??throw new InvalidOperationException("Own recovery host failed.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try{
            using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(5)};
            var ready=false;for(var i=0;i<120&&!process.HasExited;i++){try{using var ping=await http.GetAsync("/health/ready",ct);if(ping.IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}await Task.Delay(500,ct);}
            Check(ready,"normal application starts with provenance migration");
            var storage=new MongoStorage(db.Db.Client,"tdtd",new MongoStorageOptions{Prefix=prefix,
                MigrationOptions=new(){MigrationStrategy=new MigrateMongoMigrationStrategy(),BackupStrategy=new CollectionMongoBackupStrategy()}});
            var client=new BackgroundJobClient(storage);
            async Task Retry(string id){
                if(id!=assignmentId&&id!=auto.Source.WorkAssignmentId)throw new InvalidOperationException("Outside own fixture allowlist.");
                var job=client.Enqueue<WorkCompletionWorkflowService>(s=>s.ReconcileAssignmentAsync(id,CancellationToken.None));jobIds.Add(job);
                // Read the current job document, not an ordering of cross-process state-history ObjectIds.
                var watch=Stopwatch.StartNew();while(true){var current=await db.Db.GetCollection<BsonDocument>(prefix+".jobGraph")
                        .Find(new BsonDocument("_id",ObjectId.Parse(job))).SingleOrDefaultAsync(ct);
                    var status=current?.GetValue("StateName",BsonNull.Value);var name=status?.IsString==true?status.AsString:null;
                    if(name=="Succeeded")break;if(name=="Failed"||watch.Elapsed>TimeSpan.FromSeconds(90))throw new InvalidOperationException("Scoped retry job did not succeed: "+job+" "+name);
                    await Task.Delay(250,ct);}
            }
            await Retry(assignmentId);
            var after=await db.WorkAssignments.Find(a=>a.Id==assignmentId).SingleAsync(ct);
            Check(!after.CompletionProjectionPending,"old committed fixture pending clears through real retry");
            Check(after.CompletedAtUtc==before.CompletedAtUtc&&after.CompletedDate==before.CompletedDate&&after.CompletedByUserId==before.CompletedByUserId
                &&after.CompletionRevision==before.CompletionRevision&&await History(assignmentId)==historyBefore,"retry preserves original decision actor/date/revision/history");
            var queues=await db.WorkAssignmentQueueItems.Find(q=>q.WorkAssignmentId==assignmentId).ToListAsync(ct);
            var jobs=await db.WorkAssignmentMaterializeJobs.Find(j=>j.WorkAssignmentId==assignmentId).ToListAsync(ct);
            Check(queues.All(q=>!q.IsActive)&&jobs.All(j=>!j.IsActive),"old fixture queue and materializer have no active entries");
            Check(queueBefore.Where(q=>q.IsActive).All(q=>queues.Single(n=>n.Id==q.Id).UpdatedByUserId==before.CompletedByUserId)
                &&jobsBefore.All(j=>jobs.Single(n=>n.Id==j.Id).UpdatedByUserId==before.CompletedByUserId),"existing queue/materializer audit uses committed reviewer");
            var views=await db.AssignmentListDocRoles.Find(d=>d.AssignmentId==assignmentId&&!d.IsDeleted).ToListAsync(ct);
            Check(views.Count>0&&views.All(d=>d.CompletedDate==after.CompletedDate&&d.CompletedAtUtc==after.CompletedAtUtc),"real assignment read model matches completion");
            await Retry(assignmentId);
            var again=await db.WorkAssignments.Find(a=>a.Id==assignmentId).SingleAsync(ct);
            Check(!again.CompletionProjectionPending&&again.CompletionRevision==before.CompletionRevision&&await History(assignmentId)==historyBefore,"second retry is idempotent");
            var reportsAfter=await db.WorkAssignmentReports.Find(r=>r.WorkId==workId).ToListAsync(ct);
            Check(reportsAfter.Count==reportsBefore.Count&&reportsBefore.All(r=>reportsAfter.Any(n=>n.Id==r.Id&&n.PayloadHash==r.PayloadHash&&n.PayloadRevision==r.PayloadRevision&&n.Status==r.Status&&n.LifecycleRevision==r.LifecycleRevision)),"retry does not resubmit, approve or rewrite reports");
            Check((await db.WorkReportPeriods.Find(p=>p.WorkId==workId).ToListAsync(ct)).Select(p=>p.Id).Order().SequenceEqual(periodsBefore.Select(p=>p.Id).Order()),"retry creates no new period");
            await Retry(auto.Source.WorkAssignmentId);
            var automated=await db.WorkAssignments.Find(a=>a.Id==auto.Source.WorkAssignmentId).SingleAsync(ct);
            Check(automated.CompletedAtUtc.HasValue&&automated.CompletedByUserId==null&&automated.CompletionMode=="AUTO_REPORTS_AND_DEADLINE"&&!automated.CompletionProjectionPending,"automatic completion converges with null actor");
            Check(automated.CompletedDate==tdtd_be.Common.Time.WorkCompletionDate.FromUtc(automated.CompletedAtUtc!.Value),"automatic completion persists canonical civil date through BSON");
            var aq=await db.WorkAssignmentQueueItems.Find(q=>q.Id==queueId).SingleAsync(ct);
            var aj=await db.WorkAssignmentMaterializeJobs.Find(j=>j.Id==materializeId).SingleAsync(ct);
            Check(!aq.IsActive&&aq.UpdatedByUserId==null&&!aj.IsActive&&aj.UpdatedByUserId==null,"automatic queue and materializer persist null audit actor in Mongo");
            var autoHistory=await History(auto.Source.WorkAssignmentId);await Retry(auto.Source.WorkAssignmentId);
            var autoAgain=await db.WorkAssignments.Find(a=>a.Id==auto.Source.WorkAssignmentId).SingleAsync(ct);
            Check(autoAgain.CompletionRevision==automated.CompletionRevision&&autoAgain.CompletedAtUtc==automated.CompletedAtUtc&&await History(auto.Source.WorkAssignmentId)==autoHistory,"automatic retry does not repeat completion");
            Check((await db.WorkAssignmentReports.Find(r=>r.Id==auto.Source.Id).SingleAsync(ct)).PayloadHash==auto.Source.PayloadHash,"automatic completion preserves source payload");
            await FixtureProvenanceChecks.Migration(db,"completion recovery after real retry and auto completion",ct);state="PASS";
        }finally{
            if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,run+"-host.log"),(await stdout)+(await stderr),CancellationToken.None);
            await File.WriteAllTextAsync(evidence,JsonSerializer.Serialize(new{run,state,checks,workId,assignmentId,wasPending=before.CompletionProjectionPending,
                before.CompletedByUserId,autoWorkId=auto.WorkId,autoAssignmentId=auto.Source.WorkAssignmentId,queueId,materializeId,jobIds,
                oldQueueRows=queueBefore.Count,oldMaterializerRows=jobsBefore.Count,scope="PRIVATE_NORMAL_HOST_REAL_DI_SCOPED_RETRY_NO_GLOBAL_SWEEP",browserUat=false},new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
            Console.WriteLine("COMPLETION_RECOVERY_EVIDENCE "+evidence);
        }
    }
}
