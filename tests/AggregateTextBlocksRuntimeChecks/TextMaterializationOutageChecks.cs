using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Fail only this fixture's outbound calls. The shared Mongo/MinIO/Hangfire services stay running.
internal static class TextMaterializationOutageChecks
{
    internal static async Task Run(MongoDbContext db,string run,HttpClient http,IConfiguration jobConfig,
        string[] instanceIds,string[] targetIds,Action<bool,string> check,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(run,"^r8-text-20261005-[a-f0-9]{8}$")||instanceIds.Length!=2||targetIds.Length!=2)
            throw new InvalidOperationException("Outage checks require two own fixture targets.");
        var local=0;var evidence=new List<object>();var outcome="FAILED";
        void Check(bool value,string label){check(value,"P05 outage "+label);local++;}
        Task<WorkAssignmentReport> Report(int i)=>db.WorkAssignmentReports.Find(r=>r.Id==targetIds[i]&&!r.IsDeleted).SingleAsync(ct);
        async Task<AggregateInstanceState> Instance(int i)=>AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db
            .GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id",instanceIds[i])).SingleAsync(ct)).Value;
        async Task<AggregateRefreshIntent> Intent(int i,long generation)=>AggregateMongoTransaction.Read<AggregateRefreshIntent>(await db.Db
            .GetCollection<BsonDocument>(AggregateCollections.Refresh).Find(new BsonDocument("_id",AggregateRefreshService.IntentKey(instanceIds[i],generation))).SingleAsync(ct)).Value;
        var initial=await Instance(0);var workId=initial.Context.WorkId;
        _=await db.Works.Find(w=>w.Id==workId&&w.AutoCode==run+"-monthly"&&!w.IsDeleted).SingleAsync(ct);
        for(var i=0;i<2;i++)if((await Instance(i)).Context.WorkId!=workId||(await Instance(i)).Context.ReportId!=targetIds[i]
            ||(await Instance(i)).State!="DRAFT"||(await Report(i)).Status!=WorkAssignmentReportStatus.Draft)
            throw new InvalidOperationException("Outage scope escaped own Draft fixture.");
        async Task<List<string>> Frozen()=> (await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen)
            .Find(new BsonDocument("workId",workId)).Sort(new BsonDocument("_id",1)).ToListAsync(ct)).Select(r=>r.ToJson()).ToList();
        var frozenBefore=await Frozen();Check(frozenBefore.Count>0,"fixture has actual frozen history to protect");
        async Task<JsonElement> Post(string route,object body){using var response=await http.PostAsJsonAsync(route,body,AggregateCanonical.Json,ct);
            var text=await response.Content.ReadAsStringAsync(ct);if(response.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Accepted))
                throw new InvalidOperationException(route+" "+response.StatusCode+" "+text);return JsonSerializer.Deserialize<JsonElement>(text);}
        async Task<AggregatePeriodContextDto> Context(int i)=>(await Post("aggregate-v2/editor/bootstrap",new{reportId=targetIds[i]}))
            .GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        async Task<AggregateMappingApplyCommandDto> Queue(int i){var state=await Instance(i);var context=await Context(i);var before=await Report(i);
            var change=new AggregateMappingChangeDto(state.Revision,state.Overrides,state.Selection,state.UnlinkedMembers.ToList(),false);
            var token=(await Post($"aggregate-v2/instances/{instanceIds[i]}/mapping/queue-preview",new AggregateMappingPreviewCommandDto(context,change))).GetProperty("token").GetString()!;
            var command=new AggregateMappingApplyCommandDto(run+"-outage-"+i,context,change,token);
            var watch=Stopwatch.StartNew();var response=await Post($"aggregate-v2/instances/{instanceIds[i]}/mapping/queue",command);
            Check(response.GetProperty("state").GetString()=="QUEUED"&&(await Report(i)).PayloadRevision==before.PayloadRevision,"native save persists method without publishing report "+i);
            evidence.Add(new{phase="save",targetId=targetIds[i],ackMs=watch.ElapsedMilliseconds});return command;}
        MongoStorage Storage(string suffix)=>new(db.Db.Client,"tdtd",new MongoStorageOptions{Prefix="p05_outage_"+run.Replace('-','_')+suffix,
            CheckQueuedJobsStrategy=CheckQueuedJobsStrategy.Poll,QueuePollInterval=TimeSpan.FromMilliseconds(100),
            MigrationOptions=new(){MigrationStrategy=new MigrateMongoMigrationStrategy(),BackupStrategy=new CollectionMongoBackupStrategy()}});
        var previousStorage=JobStorage.Current;var previousActivator=JobActivator.Current;
        var storage=Storage("_compute");JobStorage.Current=storage;
        var payload=new WorkReportPayloadService(db);var transactions=new DynamicFlowDefinitionTransactionRunner(db,NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var config=new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string,string?>{["AggregateMapping:V2Enabled"]="true"}).Build();
        using var minio=new MinioClient().WithEndpoint(config["Minio:Endpoint"]!).WithCredentials(config["Minio:AccessKey"]!,config["Minio:SecretKey"]!)
            .WithSSL(config.GetValue<bool>("Minio:Secure")).Build();
        ServiceProvider Services(IMinioClient client)=>new ServiceCollection().AddSingleton(db).AddSingleton<IConfiguration>(config)
            .AddSingleton<IWorkReportPayloadReader>(payload).AddSingleton<IWorkReportPayloadWriter>(payload)
            .AddSingleton<IDynamicFlowDefinitionTransactionRunner>(transactions).AddSingleton(client).BuildServiceProvider();
        BackgroundJobServer Server(JobStorage own,IServiceProvider services,string suffix){JobActivator.Current=new PrivateActivator(services);
            return new(new BackgroundJobServerOptions{ServerName=run+"-outage-"+suffix,WorkerCount=1,SchedulePollingInterval=TimeSpan.FromSeconds(1),ShutdownTimeout=TimeSpan.FromSeconds(5)},own);}
        async Task Wait(Func<Task<bool>> ready,int seconds,string label){var clock=Stopwatch.StartNew();while(!await ready()){
            if(clock.Elapsed>TimeSpan.FromSeconds(seconds))throw new InvalidOperationException(label+" timed out");await Task.Delay(150,ct);}}
        async Task WaitReservation(DateTimeOffset? until,string phase){if(until==null)return;var lastLog=DateTimeOffset.MinValue;
            while(DateTimeOffset.UtcNow<=until.Value.AddMilliseconds(200)){
                if(DateTimeOffset.UtcNow-lastLog>TimeSpan.FromSeconds(20)){lastLog=DateTimeOffset.UtcNow;Console.WriteLine("P05_QUEUE_WAIT "+phase+" seconds="+Math.Max(0,(int)(until.Value-DateTimeOffset.UtcNow).TotalSeconds));}
                await Task.Delay(500,ct);
            }}
        var queued=await Queue(0);var queueState=await Instance(0);var beforeQueue=await Report(0);var reserved=await Intent(0,queueState.Generation);
        try{
            // Use the first reservation's natural wait to verify the independent second report's archive failure.
            await Queue(1);var archiveState=await Instance(1);
            var worker=new AggregateMaterializationWorker(db,payload,payload,transactions,jobConfig);
            for(var attempt=0;attempt<4;attempt++){
                await worker.Run(instanceIds[1],archiveState.Generation,ct);var state=await Intent(1,archiveState.Generation);
                if(state.State=="COMPLETED")break;
                if(state.State!="PENDING")throw new InvalidOperationException("Archive fixture computation "+state.State+" "+state.ErrorCode);
                await Task.Delay(150,ct);
            }
            Check((await Intent(1,archiveState.Generation)).State=="COMPLETED","second report materializes before snapshot upload");
            var saved=await Report(1);var savedPayload=await payload.LoadReportPayloadAsync(saved,ct);
            using var native=JsonDocument.Parse(savedPayload.TableValuesJson!);
            var table=native.RootElement.GetProperty("nativeTables").GetProperty("tables").EnumerateArray().Single(t=>t.TryGetProperty("contentRef",out _));
            var reference=table.GetProperty("contentRef").Clone();var tableId=table.GetProperty("tableId").GetString()!;
            var pageRequest=new AggregateContentReadRequest(targetIds[1],tableId,saved.PayloadRevision,null,reference,Limit:1);
            var baselinePage=await Post("aggregate-v2/content/page",pageRequest);
            var row=baselinePage.GetProperty("rows").EnumerateArray().Single();
            var partRequest=pageRequest with{RowKey=row.GetProperty("rowKey").GetString()};
            var baselinePart=await Post("aggregate-v2/content/part",partRequest);
            Check(baselinePage.GetProperty("total").GetInt32()>0&&baselinePart.GetProperty("text").GetString()!.Length>1000,"authorized content paging and long text baseline are nonempty");
            var rows=db.Db.GetCollection<BsonDocument>(AggregateCollections.ContentSnapshots);
            var intents=await rows.Find(new BsonDocument("target",targetIds[1])).ToListAsync(ct);
            var snapshot=intents.Single(r=>AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value.PayloadRevision==saved.PayloadRevision);
            var snapshotId=snapshot["_id"].AsString;
            Check(snapshot.GetValue("state","PENDING")!="READY","new snapshot is pending before its upload test");
            var wrapped=MinioUploadFault.Wrap(minio);var uploadStorage=Storage("_upload");var uploadJobs=new BackgroundJobClient(uploadStorage);
            using(var services=Services(wrapped.Client)){
                var jobId=uploadJobs.Enqueue<AggregateContentSnapshotJob>(job=>job.Run(snapshotId,CancellationToken.None));wrapped.Fault.Arm();
                using var server=Server(uploadStorage,services,"upload");var uploadClock=Stopwatch.StartNew();
                await Wait(()=>Task.FromResult(wrapped.Fault.Failures==1),30,"injected MinIO upload failure");
                var failed=await rows.Find(new BsonDocument("_id",snapshotId)).SingleAsync(ct);
                Check(failed.GetValue("state","PENDING")!="READY","upload failure never reports snapshot READY");
                Check((await Report(1)).PayloadRevision==saved.PayloadRevision&&(await Report(1)).PayloadHash==saved.PayloadHash,"upload failure preserves the committed native report");
                Check((await Post("aggregate-v2/content/page",pageRequest)).GetRawText()==baselinePage.GetRawText()
                    &&(await Post("aggregate-v2/content/part",partRequest)).GetRawText()==baselinePart.GetRawText(),"authorized page and long-text reads remain exact while archive is pending");
                await Wait(()=>Task.FromResult(uploadStorage.GetMonitoringApi().JobDetails(jobId).History.Any(h=>h.StateName=="Scheduled")),10,"Hangfire automatic retry scheduling");
                Check(uploadStorage.GetMonitoringApi().JobDetails(jobId).History.Any(h=>h.StateName=="Scheduled"),"real Hangfire schedules retry after the upload exception");
                await Wait(async()=> (await rows.Find(new BsonDocument("_id",snapshotId)).SingleAsync(ct)).GetValue("state","")=="READY",100,"real MinIO retry");
                await Wait(()=>Task.FromResult(uploadStorage.GetMonitoringApi().SucceededJobs(0,100).Any(pair=>pair.Key==jobId)),10,"upload job Succeeded");
                var archived=await rows.Find(new BsonDocument("_id",snapshotId)).SingleAsync(ct);
                Check(wrapped.Fault.Failures==1&&(await Report(1)).PayloadRevision==saved.PayloadRevision,"automatic upload retry succeeds without rewriting report payload");
                var currentRows=await rows.Find(new BsonDocument("target",targetIds[1])).ToListAsync(ct);
                Check(currentRows.Count(r=>AggregateMongoTransaction.Read<AggregateContentSnapshotIntent>(r).Value.PayloadRevision==saved.PayloadRevision)==1,"retry does not duplicate the durable snapshot intent");
                await using var archive=new MemoryStream();await minio.GetObjectAsync(new GetObjectArgs().WithBucket(archived["bucket"].AsString)
                    .WithObject(archived["objectKey"].AsString).WithCallbackStream(stream=>stream.CopyTo(archive)),ct);
                Check(Convert.ToHexString(SHA256.HashData(archive.ToArray()))==archived["sha256"].AsString,"real MinIO file readback matches stored SHA256");
                var duplicateId=uploadJobs.Enqueue<AggregateContentSnapshotJob>(job=>job.Run(snapshotId,CancellationToken.None));
                await Wait(()=>Task.FromResult(uploadStorage.GetMonitoringApi().SucceededJobs(0,100).Any(pair=>pair.Key==duplicateId)),15,"duplicate snapshot delivery");
                Check((await rows.Find(new BsonDocument("_id",snapshotId)).SingleAsync(ct)).ToJson()==archived.ToJson()
                    &&(await Report(1)).PayloadRevision==saved.PayloadRevision,"duplicate snapshot delivery leaves artifact metadata and report unchanged");
                evidence.Add(new{phase="minio",targetId=targetIds[1],snapshotId,jobId,duplicateId,elapsedMs=uploadClock.ElapsedMilliseconds,
                    failures=wrapped.Fault.Failures,sha256=archived["sha256"].AsString,bytes=archived["bytes"].ToInt64()});
            }

            await WaitReservation(reserved.DispatchUntil,"initial-dispatch");
            var failing=new FailingQueueClient();var watch=Stopwatch.StartNew();
            var sent=await AggregateMaterializationDispatch.Schedule(db,transactions,failing,100,ct,workId);
            var pending=await Intent(0,queueState.Generation);
            Check(sent==0&&failing.Jobs.Any(j=>Matches(j,instanceIds[0],queueState.Generation)),"queue send failure reaches the exact saved instance and generation");
            Check(pending.State=="PENDING"&&pending.Attempt==0&&pending.Lease==null&&pending.DispatchUntil>DateTimeOffset.UtcNow,"failed send retains a durable unclaimed pending intent");
            Check((await Report(0)).PayloadRevision==beforeQueue.PayloadRevision&&(await Report(0)).PayloadHash==beforeQueue.PayloadHash
                &&AggregateCanonical.Hash((await Instance(0)).Selection)==AggregateCanonical.Hash(queueState.Selection),"queue failure preserves prior values and saved source selection");
            var attempts=failing.Jobs.Count;await AggregateMaterializationDispatch.Schedule(db,transactions,failing,100,ct,workId);
            Check(failing.Jobs.Count==attempts,"dispatch reservation prevents a hot retry loop during queue outage");
            using(var submit=await http.PostAsJsonAsync("work-assignment-reports/"+targetIds[0]+"/submit",new SubmitWorkAssignmentReportRequest{
                CommandId=run+"-queue-outage-submit",ExpectedPayloadRevision=beforeQueue.PayloadRevision,ExpectedLifecycleRevision=beforeQueue.LifecycleRevision,LateReason="Fixture P05"},AggregateCanonical.Json,ct)){
                var error=await submit.Content.ReadAsStringAsync(ct);
                Check((int)submit.StatusCode>=400&&error.Contains("AGG_COMPUTATION_REQUIRED",StringComparison.Ordinal)
                    &&(await Report(0)).Status==WorkAssignmentReportStatus.Draft&&(await Report(0)).LifecycleRevision==beforeQueue.LifecycleRevision,
                    "normal Submit rejects unavailable pending computation without changing lifecycle");
            }
            await WaitReservation(pending.DispatchUntil,"failed-send");
            // This is the production lifecycle host bridge, scoped to this fixture; not a new scheduler.
            var recovered=await AggregateHostIntegration.DispatchAsync(db,transactions,config,100,ct,workId);
            Check(recovered>0&&storage.GetMonitoringApi().EnqueuedJobs("default",0,100).Any(pair=>Matches(pair.Value.Job,instanceIds[0],queueState.Generation)),
                "production host dispatcher re-enqueues the exact pending generation after natural reservation expiry");
            using(var services=Services(minio)){
                using var server=Server(storage,services,"compute");
                await Wait(async()=> (await Intent(0,queueState.Generation)).State=="COMPLETED",45,"queue recovery computation");
                await Wait(()=>Task.FromResult(storage.GetMonitoringApi().SucceededJobs(0,100).Any(pair=>Matches(pair.Value.Job,instanceIds[0],queueState.Generation))),10,"queue recovery Succeeded");
                var computed=await Report(0);
                Check(computed.PayloadRevision==beforeQueue.PayloadRevision+1&&(await Instance(0)).AppliedGeneration==queueState.Generation,"real queue recovery publishes exactly one new native payload");
                var replay=await Post($"aggregate-v2/instances/{instanceIds[0]}/mapping/queue",queued);
                Check(replay.GetProperty("replayed").GetBoolean()&&(await Report(0)).PayloadRevision==computed.PayloadRevision,"saved command replay after outage does not recompute or write again");
                var duplicate=new BackgroundJobClient(storage).Enqueue<AggregateMaterializationWorker>(job=>job.Run(instanceIds[0],queueState.Generation,CancellationToken.None));
                await Wait(()=>Task.FromResult(storage.GetMonitoringApi().SucceededJobs(0,100).Any(pair=>pair.Key==duplicate)),20,"queue recovery duplicate");
                Check((await Report(0)).PayloadHash==computed.PayloadHash&&(await Report(0)).PayloadRevision==computed.PayloadRevision,"duplicate recovered queue delivery is idempotent");
            }
            evidence.Add(new{phase="queue",targetId=targetIds[0],queueState.Generation,failedCalls=failing.Jobs.Count,elapsedAfterFailureMs=watch.ElapsedMilliseconds,recovered});
            Check(AggregateCanonical.Hash(await Frozen())==AggregateCanonical.Hash(frozenBefore),"outage and retry preserve all frozen history");
            outcome="PASS";
        }finally{
            JobActivator.Current=previousActivator;JobStorage.Current=previousStorage;
            await FixtureProvenanceChecks.Migration(db,"P05 after queue and MinIO outage checks",ct);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"p05-outage-"+run+".json"),JsonSerializer.Serialize(new{run,state=outcome,checks=local,workId,
                instanceIds,targetIds,evidence,scope="NORMAL_HTTP_LOGIN_REAL_MONGO_WRITER_HANGFIRE_MINIO_WITH_PRIVATE_OUTBOUND_FAULTS_NO_SHARED_RESTART",
                limitation="Queue send and one MinIO upload fail at injected client boundaries. Actual service/network outage and recurring scheduler availability are not claimed."},new JsonSerializerOptions{WriteIndented=true}),CancellationToken.None);
        }
    }
    private static bool Matches(Job? job,string instance,long generation)=>job?.Type==typeof(AggregateMaterializationWorker)
        &&string.Equals(job.Args[0] as string,instance,StringComparison.Ordinal)&&Convert.ToInt64(job.Args[1],System.Globalization.CultureInfo.InvariantCulture)==generation;
    private sealed class PrivateActivator(IServiceProvider services):JobActivator
    {
        public override object ActivateJob(Type jobType)=>ActivatorUtilities.CreateInstance(services,jobType);
    }
    private sealed class FailingQueueClient:IBackgroundJobClient
    {
        internal List<Job> Jobs{get;}=[];
        public string Create(Job job,IState state){Jobs.Add(job);throw new IOException("P05_PRIVATE_QUEUE_SEND_FAILURE");}
        public bool ChangeState(string jobId,IState state,string expectedState)=>throw new InvalidOperationException("Unexpected scheduler mutation.");
    }
}
