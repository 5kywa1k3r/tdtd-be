using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Faults affect this fresh fixture and worker process only. No lease edits, app restart or evaluator replacement.
internal static class TextMaterializationRecoveryChecks
{
    private const string Connection="mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000";
    private static MongoStorage OwnStorage(MongoDbContext db,string run,long? generation=null)=>new(db.Db.Client,"tdtd",new MongoStorageOptions{
        Prefix="p05_recovery_"+run.Replace('-','_')+(generation==null?"":"_g"+generation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        CheckQueuedJobsStrategy=CheckQueuedJobsStrategy.Poll,QueuePollInterval=TimeSpan.FromMilliseconds(100),
        MigrationOptions=new(){MigrationStrategy=new MigrateMongoMigrationStrategy(),BackupStrategy=new CollectionMongoBackupStrategy()}});
    private static Task<BsonDocument> InstanceRow(MongoDbContext db,string id,CancellationToken ct)=>
        db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",id)).SingleAsync(ct);
    private static async Task<AggregateInstanceState> Instance(MongoDbContext db,string id,CancellationToken ct)=>
        AggregateMongoTransaction.Read<AggregateInstanceState>(await InstanceRow(db,id,ct)).Value;
    private static async Task<AggregateRefreshIntent> Intent(MongoDbContext db,string id,long generation,CancellationToken ct)=>
        AggregateMongoTransaction.Read<AggregateRefreshIntent>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Refresh)
            .Find(new BsonDocument("_id",AggregateRefreshService.IntentKey(id,generation))).SingleAsync(ct)).Value;
    private static async Task AssertOwn(MongoDbContext db,string run,string instanceId,string sourceId,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(run,"^r8-text-20261005-[a-f0-9]{8}$"))throw new InvalidOperationException("Not an own recovery run.");
        var instance=await Instance(db,instanceId,ct);
        var work=await db.Works.Find(w=>w.Id==instance.Context.WorkId&&!w.IsDeleted).SingleAsync(ct);
        var source=await db.WorkAssignmentReports.Find(r=>r.Id==sourceId&&!r.IsDeleted).SingleAsync(ct);
        if(work.AutoCode!=run+"-monthly"||source.WorkId!=work.Id||instance.State!="DRAFT")throw new InvalidOperationException("Recovery scope escaped own Draft fixture.");
    }
    internal static async Task Child(string[] args)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=timeout.Token;
        var db=new MongoDbContext(Options.Create(new MongoOptions{ConnectionString=Connection,Database="tdtd"}));
        var run=args[3];var instanceId=args[4];var generation=long.Parse(args[5],System.Globalization.CultureInfo.InvariantCulture);var sourceId=args[6];
        if(args[7] is not("pause" or "recover"))throw new InvalidOperationException("Unknown private worker mode.");
        await AssertOwn(db,run,instanceId,sourceId,ct);
        if((await Instance(db,instanceId,ct)).Generation!=generation)throw new InvalidOperationException("Child generation was superseded.");
        await FixtureProvenanceChecks.Migration(db,"P05 private recovery worker startup",ct);
        var storage=OwnStorage(db,run,generation);JobStorage.Current=storage;
        var payload=new WorkReportPayloadService(db);var reader=args[7]=="pause"?new HeldPayloadReader(payload,sourceId):null;
        if(reader!=null)reader.OnEntered=()=>{Console.WriteLine("P05_WORKER_PAUSED "+Environment.ProcessId);Console.Out.Flush();};
        var runner=new DynamicFlowDefinitionTransactionRunner(db,NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var config=new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string,string?>{["AggregateMapping:V2Enabled"]="true"}).Build();
        using var minio=new MinioClient().WithEndpoint(config["Minio:Endpoint"]!).WithCredentials(config["Minio:AccessKey"]!,config["Minio:SecretKey"]!)
            .WithSSL(config.GetValue<bool>("Minio:Secure")).Build();
        using var services=new ServiceCollection().AddSingleton(db).AddSingleton<IConfiguration>(config)
            .AddSingleton<IWorkReportPayloadReader>(reader??(IWorkReportPayloadReader)payload).AddSingleton<IWorkReportPayloadWriter>(payload)
            .AddSingleton<IDynamicFlowDefinitionTransactionRunner>(runner).AddSingleton<IMinioClient>(minio).BuildServiceProvider();
        JobActivator.Current=new PrivateActivator(services);
        using var server=new BackgroundJobServer(new BackgroundJobServerOptions{ServerName=run+"-recovery-"+Environment.ProcessId,
            WorkerCount=1,SchedulePollingInterval=TimeSpan.FromSeconds(1),ShutdownTimeout=TimeSpan.FromSeconds(5)},storage);
        Console.WriteLine("P05_HANGFIRE_STARTED "+Environment.ProcessId);Console.Out.Flush();
        while(true){
            if(args[7]=="recover"&&(await Intent(db,instanceId,generation,ct)).State=="COMPLETED"
                &&storage.GetMonitoringApi().SucceededJobs(0,100).Select(pair=>pair.Value).Any(job=>job.Job?.Type==typeof(AggregateMaterializationWorker)
                    &&string.Equals(job.Job.Args[0] as string,instanceId,StringComparison.Ordinal)
                    &&Convert.ToInt64(job.Job.Args[1],System.Globalization.CultureInfo.InvariantCulture)==generation))break;
            await Task.Delay(150,ct);
        }
        Console.WriteLine("P05_WORKER_FINISHED_BY_HANGFIRE "+Environment.ProcessId);
    }
    internal static async Task Run(MongoDbContext db,string run,HttpClient http,IConfiguration config,string instanceId,string targetId,
        string sourceId,Action<bool,string> check,CancellationToken ct)
    {
        await AssertOwn(db,run,instanceId,sourceId,ct);
        // The parent harness still owns a running Hangfire server for its baseline checks.
        // Isolate callbacks dispatched by these controlled workers as well as the HTTP host queue.
        var previousStorage=JobStorage.Current;
        JobStorage.Current=OwnStorage(db,run);
        var evidence=new List<object>();var local=0;var path=Path.Combine(AppContext.BaseDirectory,"p05-materialization-recovery-"+run+".json");
        void Check(bool value,string label){check(value,"P05 materialization "+label);local++;}
        async Task<JsonElement> Post(string route,object body){using var response=await http.PostAsJsonAsync(route,body,AggregateCanonical.Json,ct);
            var text=await response.Content.ReadAsStringAsync(ct);if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))throw new InvalidOperationException(route+" "+response.StatusCode+" "+text);
            return JsonSerializer.Deserialize<JsonElement>(text);}
        Task<WorkAssignmentReport> Report()=>db.WorkAssignmentReports.Find(r=>r.Id==targetId).SingleAsync(ct);
        Task<WorkAssignmentReport> Source()=>db.WorkAssignmentReports.Find(r=>r.Id==sourceId).SingleAsync(ct);
        Task<AggregateInstanceState> State()=>Instance(db,instanceId,ct);
        async Task<AggregatePeriodContextDto> Context()=>(await Post("aggregate-v2/editor/bootstrap",new{reportId=targetId})).GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var payload=new WorkReportPayloadService(db);var runner=new DynamicFlowDefinitionTransactionRunner(db,NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var normal=new AggregateMaterializationWorker(db,payload,payload,runner,config);var mutation=0;
        async Task<AggregateInstanceState> Queue(string suffix){var state=await State();var context=await Context();var before=await Report();
            var change=new AggregateMappingChangeDto(state.Revision,state.Overrides,state.Selection,state.UnlinkedMembers.ToList(),false);
            var token=(await Post($"aggregate-v2/instances/{instanceId}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change))).GetProperty("token").GetString()!;
            await Post($"aggregate-v2/instances/{instanceId}/mapping/queue",new AggregateMappingApplyCommandDto(run+"-recovery-"+suffix,context,change,token));
            Check((await Report()).PayloadRevision==before.PayloadRevision,"saving intent does not publish values: "+suffix);return await State();}
        async Task Mutate(string operation){var source=await Source();var watch=Stopwatch.StartNew();
            await Post("work-assignment-review/reports/"+sourceId+"/"+operation,new ReportActiveRequest{CommandId=run+"-recovery-"+(++mutation),
                ExpectedPayloadRevision=source.PayloadRevision,ExpectedLifecycleRevision=source.LifecycleRevision,Comment="Dữ liệu thử P05: nguồn đổi trong lúc tính"});
            evidence.Add(new{operation,ackMs=watch.ElapsedMilliseconds,sourceId});}
        async Task Complete(long generation){for(var attempt=0;attempt<4;attempt++){
            await normal.Run(instanceId,generation,ct);var intent=await Intent(db,instanceId,generation,ct);
            if(intent.State=="COMPLETED")return;
            if(intent.State!="PENDING")throw new InvalidOperationException("Own materialization did not complete: "+intent.State+"/"+intent.ErrorCode);
            await Task.Delay(150,ct);
        }throw new InvalidOperationException("Own materialization stayed stale.");}
        Process StartChild(long generation,string mode){var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,
            CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{Assembly.GetExecutingAssembly().Location,"--database","tdtd","--own-materialization-worker",run,instanceId,
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture),sourceId,mode})start.ArgumentList.Add(arg);
            return Process.Start(start)??throw new InvalidOperationException("Private worker did not start.");}
        var originalSource=await Source();var beforeState=await State();
        if(!originalSource.IsActive||originalSource.Status!=WorkAssignmentReportStatus.Approved)throw new InvalidOperationException("Expected own active Approved source.");
        async Task<string> FrozenHash()=>AggregateCanonical.Hash((await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen)
            .Find(new BsonDocument("workId",beforeState.Context.WorkId)).Sort(new BsonDocument("_id",1)).ToListAsync(ct)).Select(r=>r.ToJson()).ToArray());
        var frozenBefore=await FrozenHash();
        var outcome="FAILED";
        try{
            // Hold an actual payload already read; native hide/show commits invalidate its lease.
            var queued=await Queue("source-race");var unchanged=await Report();var held=new HeldPayloadReader(payload,sourceId);
            var oldWorker=new AggregateMaterializationWorker(db,held,payload,runner,config);
            var running=oldWorker.Run(instanceId,queued.Generation,ct);
            try{
                await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);
                Check((await Intent(db,instanceId,queued.Generation,ct)).State=="RUNNING","source race begins after durable RUNNING claim and real payload read");
                await Mutate("deactivate");await Mutate("reactivate");await Mutate("deactivate");
            }finally{held.Release.TrySetResult();await running.WaitAsync(TimeSpan.FromSeconds(20),ct);}
            var raced=await Intent(db,instanceId,queued.Generation,ct);
            Check((await Report()).PayloadRevision==unchanged.PayloadRevision&&(await Report()).PayloadHash==unchanged.PayloadHash,"invalidated running candidate cannot write an old source result");
            Check(raced.State=="PENDING"&&raced.Lease==null,"latest hide leaves a durable pending intent after old worker exits");
            await Complete(queued.Generation);var hidden=await State();var hiddenReport=await Report();
            Check(hidden.Applied!.Preview.ContributingSources.All(p=>p.ReportId!=sourceId)&&hidden.Applied.Preview.Issues.Any(i=>i.Code=="AGG_SOURCE_INACTIVE"),"replacement job reflects the latest hidden source and reports its reason");
            Check(AggregateCanonical.Hash(hidden.Selection)==AggregateCanonical.Hash(queued.Selection),"source race keeps explicit source selection");
            Check(hiddenReport.PayloadRevision==unchanged.PayloadRevision+1,"rapid hide/show/hide publishes once after the final state");
            await normal.Run(instanceId,queued.Generation,ct);Check((await Report()).PayloadRevision==hiddenReport.PayloadRevision,"duplicate delivery after source race cannot publish twice");
            await Mutate("reactivate");await Complete((await State()).Generation);
            Check((await State()).Applied!.Preview.ContributingSources.Any(p=>p.ReportId==sourceId),"reactivation restores the current Approved contributor");

            queued=await Queue("old-generation");unchanged=await Report();held=new HeldPayloadReader(payload,sourceId);
            running=new AggregateMaterializationWorker(db,held,payload,runner,config).Run(instanceId,queued.Generation,ct);
            AggregateInstanceState next;
            try{await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);next=await Queue("new-generation");}
            finally{held.Release.TrySetResult();await running.WaitAsync(TimeSpan.FromSeconds(20),ct);}
            Check(next.Generation>queued.Generation&&(await Report()).PayloadRevision==unchanged.PayloadRevision,"saving a newer generation while running prevents old candidate publication");
            await Complete(next.Generation);var newest=await Report();
            await normal.Run(instanceId,queued.Generation,ct);
            Check((await State()).AppliedGeneration==next.Generation&&newest.PayloadRevision==unchanged.PayloadRevision+1&&(await Report()).PayloadRevision==newest.PayloadRevision,"only new generation publishes; delayed old delivery cannot overwrite it");

            queued=await Queue("process-kill");unchanged=await Report();var crashClock=Stopwatch.StartNew();
            // A new prefix for this generation contains only the real queued crash/recovery jobs.
            JobStorage.Current=OwnStorage(db,run,queued.Generation);
            var killedJobId=new BackgroundJobClient().Enqueue<AggregateMaterializationWorker>(worker=>worker.Run(instanceId,queued.Generation,CancellationToken.None));
            using(var child=StartChild(queued.Generation,"pause")){
                var stderr=child.StandardError.ReadToEndAsync();var lines=new List<string>();
                try{while(true){var line=await child.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(35),ct);
                    if(line==null)throw new InvalidOperationException("Private worker exited before barrier: "+await stderr);
                    lines.Add(line);if(line.StartsWith("P05_WORKER_PAUSED ",StringComparison.Ordinal))break;}
                    var claimed=await Intent(db,instanceId,queued.Generation,ct);
                    Check(claimed.State=="RUNNING"&&claimed.Lease!=null&&claimed.LeaseUntil>DateTimeOffset.UtcNow,"independent worker process owns a live durable lease");
                    evidence.Add(new{phase="before-kill",pid=child.Id,queued.Generation,claimed.Attempt,claimed.LeaseUntil,killedJobId,delivery="REAL_HANGFIRE_SERVER"});
                }finally{if(!child.HasExited)child.Kill(entireProcessTree:true);await child.WaitForExitAsync(CancellationToken.None);
                    await File.WriteAllLinesAsync(Path.Combine(AppContext.BaseDirectory,"p05-killed-worker-"+run+".log"),lines.Concat([await stderr]),CancellationToken.None);}
                Check(child.HasExited&&(await Report()).PayloadRevision==unchanged.PayloadRevision,"killing only the private worker leaves target payload untouched");
            }
            var stranded=await Intent(db,instanceId,queued.Generation,ct);
            await normal.Run(instanceId,queued.Generation,ct);
            Check((await Intent(db,instanceId,queued.Generation,ct)).Attempt==stranded.Attempt&&(await Report()).PayloadRevision==unchanged.PayloadRevision,"duplicate delivery cannot steal an unexpired crashed-worker lease");
            // Let the real 2-minute lease/reservation expire; never shorten it in Mongo.
            var expires=stranded.LeaseUntil!.Value;if(stranded.DispatchUntil>expires)expires=stranded.DispatchUntil.Value;
            var lastLog=DateTimeOffset.MinValue;
            while(DateTimeOffset.UtcNow<=expires.AddMilliseconds(200)){
                if(DateTimeOffset.UtcNow-lastLog>TimeSpan.FromSeconds(15)){lastLog=DateTimeOffset.UtcNow;Console.WriteLine("P05_RECOVERY_WAIT naturalLeaseSeconds="+Math.Max(0,(int)(expires-DateTimeOffset.UtcNow).TotalSeconds));}
                await Task.Delay(500,ct);
            }
            var dispatched=await AggregateMaterializationDispatch.Schedule(db,runner,new BackgroundJobClient(),100,ct,beforeState.Context.WorkId);
            var queuedJobs=JobStorage.Current.GetMonitoringApi().EnqueuedJobs("default",0,100);
            Check(dispatched>0&&queuedJobs.Select(pair=>pair.Value).Any(job=>job.Job?.Type==typeof(AggregateMaterializationWorker)
                &&string.Equals(job.Job.Args[0] as string,instanceId,StringComparison.Ordinal)
                &&Convert.ToInt64(job.Job.Args[1],System.Globalization.CultureInfo.InvariantCulture)==queued.Generation),
                "existing dispatcher enqueues the exact naturally expired instance and generation");
            using(var recovered=StartChild(queued.Generation,"recover")){
                var stdout=recovered.StandardOutput.ReadToEndAsync();var stderr=recovered.StandardError.ReadToEndAsync();
                try{await recovered.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(45),ct);
                    var output=await stdout;var errors=await stderr;
                    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"p05-recovered-worker-"+run+".log"),output+errors,ct);
                    Check(recovered.ExitCode==0&&output.Contains("P05_WORKER_FINISHED_BY_HANGFIRE",StringComparison.Ordinal),
                        "fresh Hangfire Server consumes the recovery job successfully after a real process kill: "+errors);
                    evidence.Add(new{phase="recovered",pid=recovered.Id,elapsedMs=crashClock.ElapsedMilliseconds,dispatched});
                }finally{if(!recovered.HasExited)recovered.Kill(entireProcessTree:true);await recovered.WaitForExitAsync(CancellationToken.None);}
            }
            var recoveredIntent=await Intent(db,instanceId,queued.Generation,ct);var recoveredReport=await Report();
            Check(recoveredIntent.State=="COMPLETED"&&recoveredIntent.Attempt==stranded.Attempt+1,"recovery claims once and completes the persisted intent");
            Check(recoveredReport.PayloadRevision==unchanged.PayloadRevision+1&&(await State()).AppliedGeneration==queued.Generation,"recovered process writes exactly one authoritative payload");
            await normal.Run(instanceId,queued.Generation,ct);
            Check((await Report()).PayloadHash==recoveredReport.PayloadHash&&(await Report()).PayloadRevision==recoveredReport.PayloadRevision,"replayed delivery after recovery is idempotent");
            var frozenAfter=await FrozenHash();
            Check(frozenBefore==frozenAfter,"source races and process recovery preserve submitted historical snapshots");
            outcome="PASS";
        }finally{try{
            if(!(await Source()).IsActive){await Mutate("reactivate");await Complete((await State()).Generation);}
            await FixtureProvenanceChecks.Migration(db,"P05 after materialization race and process recovery",ct);
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{run,state=outcome,checks=local,targetId,sourceId,instanceId,workId=beforeState.Context.WorkId,evidence,
                scope="NORMAL_LOGIN_HTTP_REAL_WRITER_SOURCE_IO_BARRIER_ACTUAL_PRIVATE_WORKER_PROCESS_KILL_NATURAL_LEASE_RECOVERY_NO_SHARED_RESTART_NO_BROWSER_UAT",
                limitation="The existing dispatcher is invoked explicitly after natural lease expiry; this does not test recurring scheduler availability or queue/storage outages."},new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
            }finally{JobStorage.Current=previousStorage;}
        }
    }
    private sealed class PrivateActivator(IServiceProvider services):JobActivator
    {
        public override object ActivateJob(Type jobType)=>ActivatorUtilities.CreateInstance(services,jobType);
    }
    private sealed class HeldPayloadReader(IWorkReportPayloadReader inner,string sourceId):IWorkReportPayloadReader
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? OnEntered;
        public async Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(WorkAssignmentReport report,CancellationToken ct=default){
            var payload=await inner.LoadReportPayloadAsync(report,ct);
            if(report.Id==sourceId){Entered.TrySetResult();OnEntered?.Invoke();await Release.Task.WaitAsync(ct);}return payload;}
        public Task<string?> LoadReportTableBlockAsync(WorkAssignmentReport report,string blockId,CancellationToken ct=default)=>inner.LoadReportTableBlockAsync(report,blockId,ct);
        public Task<IReadOnlyDictionary<string,string>> LoadReportTableBlocksAsync(WorkAssignmentReport report,IEnumerable<string> blockIds,CancellationToken ct=default)=>inner.LoadReportTableBlocksAsync(report,blockIds,ct);
    }
}
