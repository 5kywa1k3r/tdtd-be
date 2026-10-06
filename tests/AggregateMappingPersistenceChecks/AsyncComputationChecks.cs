using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class AsyncComputationChecks
{
    internal static async Task Run(Action<bool,string> check, Func<Func<Task>,string,Task> error)
    {
        var store = new TestStore(); store.Reports["reportB"] = new();
        var reader = new TestReader(store) { RejectPreviewInTransaction = true };
        var tokens = new AggregateConfirmationTokens(new byte[32]);
        var service = new AggregateCommandService(store, reader, tokens);
        var context = TestReader.Context();
        AggregateCommandContext Command(string id) => new(id,"ASYNC", "reportB","B","sessionB",DateTimeOffset.UtcNow);
        var config = await service.CreateConfigAsync(Command("async-config"), context,
            new("async-config", context.BindingId, TestReader.TargetForm, TestReader.Recipe()), default);
        var created = await service.CreateInstanceAsync(Command("async-instance"), context, config.Id, default);
        AggregateInstanceState Instance() => store.Value<AggregateInstanceState>(AggregateCollections.Instances, created.Id)!;
        AggregateRefreshIntent Intent() => store.Value<AggregateRefreshIntent>(AggregateCollections.Refresh, AggregateRefreshService.IntentKey(created.Id, Instance().Generation))!;
        async Task Queue(string id) {
            var change = new AggregateMappingChange(Instance().Revision, null, Instance().Selection, [], false);
            var signed = await service.PreviewQueuedMappingAsync(Command(id), context, created.Id, change, default);
            var calls = reader.PreviewCalls; var payload = store.Reports["reportB"].Payload;
            var result = await service.SaveQueuedMappingAsync(Command(id), context, created.Id, change, signed.Token, default);
            check(result.State=="QUEUED" && reader.PreviewCalls==calls && store.Reports["reportB"].Payload==payload, "ASJ1 save and metadata confirmation never evaluate or write target");
            var replay = await service.SaveQueuedMappingAsync(Command(id), context, created.Id, change, signed.Token, default);
            check(replay.Replayed && Intent().State=="PENDING", "ASJ1 receipt replay preserves one durable generation");
            check(store.Rows.Where(row=>row.Key.Collection==AggregateCollections.Refresh&&row.Value.Keys.Contains("PENDING")).Count()==0,
                "ASJ1 durable intents cannot be consumed by legacy PENDING dispatcher");
        }
        var lifecycle = new AggregateLifecycleParticipant(store, reader, tokens);
        await Queue("async-save");
        await error(()=>lifecycle.PreviewSubmissionAsync(Command("submit-pending"),context,default), "AGG_COMPUTATION_REQUIRED");
        var refresh = new AggregateRefreshService(store, reader, materialized:true);
        check(await refresh.RunAsync(created.Id, Instance().Generation, default)=="COMPLETED" && Instance().AppliedInputStamp!=null && Instance().AppliedGeneration==Instance().Generation && store.Reports["reportB"].Value=="30", "ASJ1 complete worker publishes materialized result and stamp");
        var calls = reader.PreviewCalls;
        await lifecycle.PreviewSubmissionAsync(Command("submit-ready"), context, default);
        check(reader.PreviewCalls==calls, "ASJ1 Submit preview checks metadata without reevaluating materialized sources");
        reader.SourceRevision++;
        await error(()=>lifecycle.PreviewSubmissionAsync(Command("submit-stale"), context, default), "AGG_INPUT_STALE");
        await Queue("async-save-cancel");
        reader.AfterPreview=async()=>{await service.ChangeComputationAsync(Command("cancel"),context,created.Id,Instance().Revision,false,default);};
        var oldPayload=store.Reports["reportB"].Payload;
        await refresh.RunAsync(created.Id, Instance().Generation, default);
        check(Intent().State=="CANCELLED" && store.Reports["reportB"].Payload==oldPayload && Instance().Applied!=null, "ASJ1 cancellation during evaluation cannot overwrite prior result");
        await error(()=>lifecycle.PreviewSubmissionAsync(Command("submit-cancel"),context,default),"AGG_COMPUTATION_REQUIRED");
        await service.ChangeComputationAsync(Command("retry"),context,created.Id,Instance().Revision,true,default);
        check(await refresh.RunAsync(created.Id,Instance().Generation,default)=="COMPLETED", "ASJ1 explicit retry completes saved method");
        oldPayload=store.Reports["reportB"].Payload;
        check(await refresh.RunAsync(created.Id,Instance().Generation,default)=="NO_WORK" && store.Reports["reportB"].Payload==oldPayload, "ASJ1 duplicate job does not repeat payload write");
        await Queue("async-supersede-start");
        var oldGeneration=Instance().Generation;
        reader.AfterPreview=()=>Queue("async-supersede-new");
        await refresh.RunAsync(created.Id, oldGeneration, default);
        check(Instance().Generation>oldGeneration && Intent().State=="PENDING" && store.Reports["reportB"].Payload==oldPayload, "ASJ1 newer saved generation supersedes running candidate");
        reader.AfterPreview=()=>{reader.SourceRevision++;return Task.CompletedTask;};
        await refresh.RunAsync(created.Id,Instance().Generation,default);
        check(Intent().State=="PENDING" && store.Reports["reportB"].Payload==oldPayload, "ASJ1 changed input during compute requeues without writing stale value");
        check(await refresh.RunAsync(created.Id,Instance().Generation,default)=="COMPLETED", "ASJ1 latest stable input subsequently publishes");
        await Queue("async-expired-worker");
        await store.ExecuteAsync(async(tx,ct)=>{
            var key=AggregateRefreshService.IntentKey(created.Id,Instance().Generation);
            var row=(await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh,key,ct))!;
            await tx.PutAsync(AggregateCollections.Refresh,key,row.Version,row.Value with {State="RUNNING",Lease="dead-worker",LeaseUntil=DateTimeOffset.UtcNow.AddMinutes(-1)},context.WorkId,"reportB",["RUNNING"],ct);
            return true;
        },default);
        check(await refresh.RunAsync(created.Id,Instance().Generation,default)=="COMPLETED" && Intent().Attempt>0,"ASJ1 expired dead worker lease is reclaimed and publishes once");
        await Queue("async-revoked-worker");oldPayload=store.Reports["reportB"].Payload;reader.Authorized=false;
        check(await refresh.RunAsync(created.Id,Instance().Generation,default)=="FAILED" && store.Reports["reportB"].Payload==oldPayload,"ASJ1 revoked worker authority fails closed and preserves payload");
        reader.Authorized=true;
        var legacyChange=new AggregateMappingChange(Instance().Revision,null,Instance().Selection,[],false);
        var legacyPreview=await service.PreviewChangeAsync(Command("legacy-preview"),context,created.Id,legacyChange,default);
        await service.ApplyAsync(Command("legacy-apply"),context,created.Id,legacyChange,legacyPreview.Token,default);
        check(Instance().AppliedInputStamp==null && Instance().AppliedGeneration==Instance().Generation,"ASJ1 synchronous API compatibility clears materialized stamp instead of falsely blocking Submit");
    }
}
