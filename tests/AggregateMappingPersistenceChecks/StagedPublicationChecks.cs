using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class StagedPublicationChecks
{
    internal static async Task Run(Action<bool, string> check, Func<Func<Task>, string, Task> error)
    {
        var store = new TestStore(); store.Reports["reportB"] = new();
        var reader = new TestReader(store) { RejectPreviewInTransaction = true };
        var now = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        AggregateCommandContext Command(string id, string op = "APPLY") => new(id, op, "reportB", "B", "sessionB", now);
        var tokens = new AggregateConfirmationTokens(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var service = new AggregateCommandService(store, reader, tokens);
        var context = TestReader.Context();
        var config = await service.CreateConfigAsync(Command("stage-config"), context,
            new("stage-config", context.BindingId, TestReader.TargetForm, TestReader.Recipe()), default);
        var created = await service.CreateInstanceAsync(Command("stage-instance"), context, config.Id, default);
        AggregateInstanceState Instance() => store.Value<AggregateInstanceState>(AggregateCollections.Instances, created.Id)!;
        AggregateMappingChange Change() => new(Instance().Revision, null, Instance().Selection, [], false);
        var change = Change(); var preview = await service.PreviewChangeAsync(Command("stage-preview"), context, created.Id, change, default);
        await service.ApplyAsync(Command("stage-apply"), context, created.Id, change, preview.Token, default);
        check(store.Reports["reportB"].Payload == 2, "Apply stages artifacts outside publication transaction and still writes atomically");
        var calls = reader.PreviewCalls;
        var replay = await service.ApplyAsync(Command("stage-apply"), context, created.Id, change, preview.Token, default);
        check(replay.Replayed && reader.PreviewCalls == calls, "Apply receipt replay never restages artifacts");

        var impact = new AggregateConfigImpactRequestDto(1, TestReader.Recipe(), [new(created.Id, Instance().Revision)], []);
        var preparedImpact = await service.PreviewConfigAsync(Command("stage-impact", "CONFIG_REVISION"), context, config.Id, impact, default);
        await service.SaveConfigAsync(Command("stage-save", "CONFIG_REVISION"), context, config.Id, impact, preparedImpact.Token!, default);
        check(Instance().ConfigRevision == 2 && store.Reports["reportB"].Payload == 3,
            "common revision stages selected Draft results outside transaction and publishes head with payload");
        calls = reader.PreviewCalls;
        replay = await service.SaveConfigAsync(Command("stage-save", "CONFIG_REVISION"), context, config.Id, impact, preparedImpact.Token!, default);
        check(replay.Replayed && reader.PreviewCalls == calls && store.Reports["reportB"].Payload == 3,
            "revision receipt replay never restages or rewrites selected Draft");

        change = Change(); preview = await service.PreviewChangeAsync(Command("race-preview"), context, created.Id, change, default);
        reader.AfterPreview = () => store.ExecuteAsync(async (tx, ct) =>
        {
            var row = await AggregateCommandService.Required<AggregateInstanceState>(tx, AggregateCollections.Instances, created.Id, ct);
            await AggregateCommandService.PutInstance(tx, row.Value with { Revision = row.Value.Revision + 1 }, row.Version, ct);
            return true;
        }, default);
        await error(() => service.ApplyAsync(Command("race-apply"), context, created.Id, change, preview.Token, default), "AGG_REVISION_CONFLICT");
        check(store.Reports["reportB"].Payload == 3, "instance edited after staging cannot overwrite current Draft");
        check(store.Value<AggregateReceipt>(AggregateCollections.Receipts, AggregateCanonical.Key("B", "reportB", "APPLY", "race-apply")) == null,
            "staging race cannot create success receipt");

        impact = new(2, TestReader.Recipe(), [new(created.Id, Instance().Revision)], []);
        preparedImpact = await service.PreviewConfigAsync(Command("race-impact", "CONFIG_REVISION"), context, config.Id, impact, default);
        reader.AfterPreview = () => store.ExecuteAsync(async (tx, ct) =>
        {
            var row = await AggregateCommandService.Required<AggregateInstanceState>(tx, AggregateCollections.Instances, created.Id, ct);
            await AggregateCommandService.PutInstance(tx, row.Value with { Revision = row.Value.Revision + 1 }, row.Version, ct);
            return true;
        }, default);
        await error(() => service.SaveConfigAsync(Command("race-save", "CONFIG_REVISION"), context, config.Id, impact, preparedImpact.Token!, default), "AGG_REVISION_CONFLICT");
        check(store.Value<AggregateConfigHead>(AggregateCollections.Configs, config.Id)!.HeadRevision == 2 && store.Reports["reportB"].Payload == 3,
            "selected Draft race leaves config head and all payloads unchanged");

        await store.ExecuteAsync(async (tx, ct) => { await AggregateRefreshService.EnqueueAsync(tx, Instance(), "stage-refresh", ct); return true; }, default);
        var refresh = new AggregateRefreshService(store, reader);
        var generation = Instance().Generation;
        reader.AfterPreview = () => store.ExecuteAsync(async (tx, ct) =>
        {
            var row = await AggregateCommandService.Required<AggregateInstanceState>(tx, AggregateCollections.Instances, created.Id, ct);
            await AggregateCommandService.PutInstance(tx, row.Value with { Revision = row.Value.Revision + 1 }, row.Version, ct);
            return true;
        }, default);
        check(await refresh.RunAsync(created.Id, generation, default) == "NO_WORK" && store.Reports["reportB"].Payload == 3,
            "refresh staged before concurrent Draft edit cannot overwrite current payload");
        check(store.Value<AggregateRefreshIntent>(AggregateCollections.Refresh, AggregateRefreshService.IntentKey(created.Id, generation))!.State == "PENDING",
            "refresh staging race keeps work pending for the existing dispatcher");
        check(await refresh.RunAsync(created.Id, generation, default) == "COMPLETED" && store.Reports["reportB"].Payload == 4,
            "refresh stages outside publication transaction and next dispatch commits once");
        calls = reader.PreviewCalls;
        check(await refresh.RunAsync(created.Id, generation, default) == "NO_WORK" && reader.PreviewCalls == calls,
            "completed refresh replay does not calculate or stage again");
    }
}
