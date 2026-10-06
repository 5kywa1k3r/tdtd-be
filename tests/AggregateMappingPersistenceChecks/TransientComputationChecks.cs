using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class TransientComputationChecks
{
    private sealed class RolledBackTransient : Exception;
    private sealed class UnknownCommit : Exception;
    internal static async Task Run(Action<bool,string> check)
    {
        var store=new TestStore();store.Reports["reportB"]=new();
        var reader=new TestReader(store){RejectPreviewInTransaction=true};
        var service=new AggregateCommandService(store,reader,new AggregateConfirmationTokens(new byte[32]));
        var context=TestReader.Context();
        AggregateCommandContext Command(string id)=>new(id,"ASYNC","reportB","B","sessionB",DateTimeOffset.UtcNow);
        var config=await service.CreateConfigAsync(Command("sl-config"),context,new("sl-config",context.BindingId,TestReader.TargetForm,TestReader.Recipe()),default);
        var instance=await service.CreateInstanceAsync(Command("sl-instance"),context,config.Id,default);
        AggregateInstanceState Instance()=>store.Value<AggregateInstanceState>(AggregateCollections.Instances,instance.Id)!;
        AggregateRefreshIntent Intent()=>store.Value<AggregateRefreshIntent>(AggregateCollections.Refresh,AggregateRefreshService.IntentKey(instance.Id,Instance().Generation))!;
        var worker=new AggregateRefreshService(store,reader,true,e=>e is RolledBackTransient);
        Task<string> Run()=>worker.RunAsync(instance.Id,Instance().Generation,default);
        async Task Queue(string name){
            var change=new AggregateMappingChange(Instance().Revision,null,Instance().Selection,[],false);
            var preview=await service.PreviewQueuedMappingAsync(Command(name),context,instance.Id,change,default);
            await service.SaveQueuedMappingAsync(Command(name),context,instance.Id,change,preview.Token,default);
        }
        async Task Due(){
            await store.ExecuteAsync(async(tx,ct)=>{
                var key=AggregateRefreshService.IntentKey(instance.Id,Instance().Generation);
                var row=(await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh,key,ct))!;
                await tx.PutAsync(AggregateCollections.Refresh,key,row.Version,row.Value with{RetryNotBefore=DateTimeOffset.UtcNow.AddSeconds(-1)},context.WorkId,"reportB",["ASYNC_PENDING"],ct);return true;
            },default);
        }
        await Queue("sl-exhaust");store.AfterTargetWriteFailure=new RolledBackTransient();
        for(var attempt=1;attempt<=5;attempt++){
            var start=DateTimeOffset.UtcNow;var state=await Run();
            check(store.Reports["reportB"].Payload==1&&Instance().Applied==null,"SL1 rolled-back partial write preserves target "+attempt);
            if(attempt==5){check(state=="FAILED"&&Intent().Attempt==5&&Intent().RetryNotBefore==null&&Intent().ErrorCode=="AGG_COMPUTATION_RETRY_EXHAUSTED","SL1 stops after five claims with explicit exhausted error");break;}
            var expected=new[]{5,15,45,120}[attempt-1];
            check(state=="PENDING"&&Intent().RetryNotBefore>=start.AddSeconds(expected)&&Intent().RetryNotBefore<DateTimeOffset.UtcNow.AddSeconds(expected+1.1),"SL1 durable retry interval and jitter "+attempt);
            var calls=reader.PreviewCalls;
            check(await Run()=="NO_WORK"&&Intent().Attempt==attempt&&reader.PreviewCalls==calls,"SL1 duplicate early delivery does not claim or evaluate "+attempt);
            await Due();
        }
        check(await Run()=="NO_WORK","SL1 exhausted failure does not loop automatically");
        store.AfterTargetWriteFailure=null;
        await service.ChangeComputationAsync(Command("sl-manual-retry"),context,instance.Id,Instance().Revision,true,default);
        check(await Run()=="COMPLETED"&&Intent().Attempt==1&&store.Reports["reportB"].Payload==2,"SL1 explicit retry resets attempt and publishes once");

        await Queue("sl-fresh-input");store.AfterTargetWriteFailure=new RolledBackTransient();await Run();
        store.AfterTargetWriteFailure=null;reader.SourceRevision++;reader.SourceValue=72;await Due();
        check(await Run()=="COMPLETED"&&store.Reports["reportB"].Value=="72","SL1 retry evaluates changed source rather than old candidate");

        await Queue("sl-revoke");store.AfterTargetWriteFailure=new RolledBackTransient();await Run();
        var before=store.Reports["reportB"];store.AfterTargetWriteFailure=null;reader.Authorized=false;await Due();
        check(await Run()=="FAILED"&&store.Reports["reportB"]==before&&Intent().RetryNotBefore==null,"SL1 revoked authority is not retried as transient");reader.Authorized=true;

        await Queue("sl-cancel");store.AfterTargetWriteFailure=new RolledBackTransient();await Run();
        await service.ChangeComputationAsync(Command("sl-cancel-now"),context,instance.Id,Instance().Revision,false,default);
        store.AfterTargetWriteFailure=null;
        check(await Run()=="NO_WORK"&&Intent().State=="CANCELLED"&&store.Reports["reportB"]==before,"SL1 cancellation while waiting prevents write");

        await Queue("sl-old");store.AfterTargetWriteFailure=new RolledBackTransient();await Run();await Due();var oldGeneration=Instance().Generation;
        await Queue("sl-new");store.AfterTargetWriteFailure=null;
        await worker.RunAsync(instance.Id,oldGeneration,default);
        check(Intent().State=="PENDING"&&store.Reports["reportB"]==before,"SL1 delayed old generation cannot consume or publish new generation");
        check(await Run()=="COMPLETED"&&store.Reports["reportB"].Payload==before.Payload+1,"SL1 new generation publishes exactly once");

        await Queue("sl-unknown-committed");before=store.Reports["reportB"];store.AfterTargetCommitFailure=new UnknownCommit();
        check(await Run()=="COMPLETED"&&Intent().State=="COMPLETED"&&store.Reports["reportB"].Payload==before.Payload+1,"SL1 unknown commit response reconciles durable completed intent");
        check(await Run()=="NO_WORK","SL1 unknown committed delivery does not repeat write");
        await Queue("sl-unknown-unconfirmed");before=store.Reports["reportB"];store.AfterTargetWriteFailure=new UnknownCommit();
        check(await Run()=="FAILED"&&Intent().RetryNotBefore==null&&store.Reports["reportB"]==before,"SL1 unknown commit without durable completion is not treated as rolled-back transient");
    }
}
