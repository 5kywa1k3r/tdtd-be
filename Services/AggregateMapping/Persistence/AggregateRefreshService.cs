namespace tdtd_be.Services.AggregateMapping.Persistence;

// Work handler for an existing outbox dispatcher. No timer, Hangfire server or second scheduler.
internal sealed class AggregateRefreshService(IAggregateTransactionStore store, IAggregateCommandReader reader)
{
    internal static async Task InvalidateAsync(IAggregateTransaction tx, string workId, IReadOnlyList<string> keys, string eventId, CancellationToken ct)
    {
        var dependencies = new Dictionary<string, AggregateDependencyState>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
            foreach (var item in await tx.QueryAsync<AggregateDependencyState>(AggregateCollections.Dependencies, new(workId, DependencyKey: key), ct))
                dependencies[item.Value.InstanceId] = item.Value;
        foreach (var dependency in dependencies.Values)
        {
            var stored = await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, dependency.InstanceId, ct);
            if (stored == null || stored.Value.State != "DRAFT" || stored.Value.Generation != dependency.Generation) continue;
            await EnqueueAsync(tx, stored.Value, eventId, ct);
        }
    }
    internal static async Task EnqueueAsync(IAggregateTransaction tx, AggregateInstanceState instance, string eventId, CancellationToken ct)
    {
        var id = IntentKey(instance.Id, instance.Generation);
        var eventKey = AggregateCanonical.Key("INVALIDATE", id, eventId);
        if (await tx.GetAsync<AggregateReceipt>(AggregateCollections.Receipts, eventKey, ct) != null) return;
        var current = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, ct);
        var intent = new AggregateRefreshIntent(instance.Id, instance.Generation, eventId, instance.AuthorityUserId, "PENDING", null, null);
        await tx.PutAsync(AggregateCollections.Refresh, id, current?.Version ?? 0, intent, instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), ["PENDING"], ct);
        await tx.PutAsync(AggregateCollections.Receipts, eventKey, 0, new AggregateReceipt(AggregateCanonical.Hash(eventId),
            instance.AuthorityUserId, "INVALIDATE", System.Text.Json.JsonSerializer.SerializeToElement(new { instance.Id, instance.Generation })),
            instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), [], ct);
    }
    internal Task<string> RunAsync(string instanceId, long generation, CancellationToken ct)
        => store.ExecuteAsync(async (tx, token) =>
        {
            var id = IntentKey(instanceId, generation);
            var intent = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
            if (intent == null || intent.Value.State != "PENDING") return "NO_WORK";
            var stored = await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token);
            if (stored == null || stored.Value.Generation != generation || stored.Value.State != "DRAFT")
            {
                await Finish("DISCARDED", null, null); return "DISCARDED";
            }
            var instance = stored.Value;
            AggregateCommitAuthority authority; AggregateRecipeBundle bundle;
            try
            {
                authority = await reader.AuthorizeAsync(instance.Context, intent.Value.AuthorityUserId, "aggregate-refresh:" + instanceId, token);
                AggregateCommandService.Allow(AggregateAction.RefreshDraft, authority);
                var version = (await AggregateCommandService.Required<AggregateConfigVersion>(tx, AggregateCollections.Versions,
                    AggregateCommandService.VersionKey(instance.ConfigId, instance.ConfigRevision), token)).Value;
                var recipe = AggregateOverlay.Effective(version, instance);
                var preview = await reader.PreviewAsync(instance, recipe, authority, token); AggregateCommandService.ValidPreview(preview);
                bundle = new(recipe, preview);
            }
            catch (AggregatePreviewException ex)
            { await Finish("FAILED", ex.Code, null); return "FAILED"; }
            // Errors after fencing/writing must abort the transaction, not be converted to success.
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(instance.Context), AggregateTargetIdentity.Id(instance.Context)!, token);
            await tx.FenceAsync(authority, bundle.Preview.Preview.LinkedSources, bundle.Preview.Preview.Coverage, token);
            var members = bundle.Recipe.Nodes.Single(n => n.Kind == "TARGET").Inputs.Select(p => p.MemberId!).ToArray();
            var next = instance with { Revision = checked(instance.Revision + 1), Context = authority.Read.Context, Applied = bundle.Preview };
            await tx.WriteTargetAsync(authority, new(next, bundle.Recipe, bundle.Preview, members), token);
            await AggregateCommandService.PutInstance(tx, next, stored.Version, token);
            await AggregateCommandService.Dependencies(tx, next, bundle.Preview, token);
            await Finish("COMPLETED", null, AggregateCanonical.Hash(bundle.Preview.Diff));
            return "COMPLETED";

            Task Finish(string state, string? error, string? diff) => tx.PutAsync(AggregateCollections.Refresh, id, intent.Version,
                intent.Value with { State = state, ErrorCode = error, DiffReference = diff }, stored?.Value.Context.WorkId ?? "",
                stored == null ? null : AggregateTargetIdentity.Id(stored.Value.Context), [], token);
        }, ct);
    internal static string IntentKey(string instance, long generation) => AggregateCanonical.Key(instance, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private sealed record AggregateRecipeBundle(tdtd_be.DTOs.AggregateMapping.AggregateRecipeDto Recipe, AggregatePreviewEnvelope Preview);
}
