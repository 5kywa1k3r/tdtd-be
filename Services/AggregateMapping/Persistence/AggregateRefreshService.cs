namespace tdtd_be.Services.AggregateMapping.Persistence;

// Work handler for an existing outbox dispatcher. No timer, Hangfire server or second scheduler.
internal sealed class AggregateRefreshService(IAggregateTransactionStore store, IAggregateCommandReader reader, bool materialized = false,
    Func<Exception, bool>? canRetryRolledBackTransaction = null)
{
    private string? activeLease;
    internal const string AsyncProfile = "DURABLE_COMPUTATION_V1";
    internal static string StateKey(AggregateInstanceState? instance, string state)
        => instance?.ComputationProfile == AsyncProfile || instance?.AppliedInputStamp != null ? "ASYNC_" + state : state;
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
        await tx.PutAsync(AggregateCollections.Refresh, id, current?.Version ?? 0, intent, instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), [StateKey(instance,"PENDING")], ct);
        await tx.PutAsync(AggregateCollections.Receipts, eventKey, 0, new AggregateReceipt(AggregateCanonical.Hash(eventId),
            instance.AuthorityUserId, "INVALIDATE", System.Text.Json.JsonSerializer.SerializeToElement(new { instance.Id, instance.Generation })),
            instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), [], ct);
    }
    internal async Task<string> RunAsync(string instanceId, long generation, CancellationToken ct)
    {
        var id = IntentKey(instanceId, generation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (materialized) timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var lease = Guid.NewGuid().ToString("N");
        var capture = await store.ExecuteAsync(async (tx, token) =>
        {
            var intent = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
            if (intent == null || intent.Value.RetryNotBefore > DateTimeOffset.UtcNow
                || !(intent.Value.State == "PENDING" || intent.Value.State == "RUNNING" && intent.Value.LeaseUntil < DateTimeOffset.UtcNow)) return null;
            var claimed = intent.Value with { State = "RUNNING", Lease = lease, LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2),
                Attempt = checked(intent.Value.Attempt + 1), Progress = null, RetryNotBefore = null };
            var stored = await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token);
            await tx.PutAsync(AggregateCollections.Refresh, id, intent.Version, claimed, stored?.Value.Context.WorkId ?? "",
                stored == null ? null : AggregateTargetIdentity.Id(stored.Value.Context), [StateKey(stored?.Value,"RUNNING")], token);
            var version = stored != null && stored.Value.Generation == generation && stored.Value.State == "DRAFT"
                ? await AggregateCommandService.Required<AggregateConfigVersion>(tx, AggregateCollections.Versions,
                    AggregateCommandService.VersionKey(stored.Value.ConfigId, stored.Value.ConfigRevision), token) : null;
            return new AggregateRefreshCapture(intent with { Value = claimed }, stored, version == null ? null : AggregateOverlay.Effective(version.Value, stored!.Value));
        }, ct);
        if (capture == null) return "NO_WORK";
        activeLease = lease;
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var heartbeatAt = DateTimeOffset.UtcNow.AddSeconds(30);
        var monitor = Observe();
        async Task Observe()
        {
            try { while (true) {
                await Task.Delay(1000, monitorStop.Token);
                var owned = await store.ExecuteAsync(async (tx, token) => {
                    var row = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
                    if (row?.Value.State != "RUNNING" || row.Value.Lease != lease) return false;
                    if (DateTimeOffset.UtcNow >= heartbeatAt) {
                        await tx.PutAsync(AggregateCollections.Refresh, id, row.Version,
                            row.Value with { LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2) },
                            capture.Stored?.Value.Context.WorkId ?? "", capture.Stored == null ? null : AggregateTargetIdentity.Id(capture.Stored.Value.Context), [StateKey(capture.Stored?.Value,"RUNNING")], token);
                        heartbeatAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    }
                    return true;
                }, monitorStop.Token);
                if (!owned) { timeout.Cancel(); return; }
            } } catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch { timeout.Cancel(); }
        }
        AggregateCommitAuthority? authority = null; AggregateRecipeBundle? bundle = null; string? preparationError = null;
        string? inputStamp = null;
        try {
        if (capture.Recipe != null)
        {
            try
            {
                authority = await reader.AuthorizeAsync(capture.Stored!.Value.Context, capture.Intent.Value.AuthorityUserId, "aggregate-refresh:" + instanceId, timeout.Token);
                AggregateCommandService.Allow(AggregateAction.RefreshDraft, authority);
                if (materialized) {
                    inputStamp = await reader.InputStampAsync(capture.Stored.Value, capture.Recipe, authority, timeout.Token);
                    await store.ExecuteAsync(async (tx, token) => {
                        var row = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
                        if (row?.Value.State != "RUNNING" || row.Value.Lease != lease) throw new OperationCanceledException();
                        await tx.PutAsync(AggregateCollections.Refresh, id, row.Version, row.Value with { InputStamp = inputStamp },
                            capture.Stored.Value.Context.WorkId, AggregateTargetIdentity.Id(capture.Stored.Value.Context), [StateKey(capture.Stored.Value,"RUNNING")], token);
                        return true;
                    }, timeout.Token);
                }
                var preview = await reader.PreviewAsync(capture.Stored.Value, capture.Recipe, authority, timeout.Token); AggregateCommandService.ValidPreview(preview);
                if (materialized && inputStamp != await reader.InputStampAsync(capture.Stored.Value, capture.Recipe, authority, timeout.Token))
                    throw new AggregatePreviewException("AGG_INPUT_STALE");
                bundle = new(capture.Recipe, preview);
            }
            catch (AggregatePreviewException ex) { preparationError = ex.Code; }
        }
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var intent = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
            if (intent == null || intent.Value.State != "RUNNING" || intent.Value.Lease != lease || intent.Value.LeaseUntil < DateTimeOffset.UtcNow) return "NO_WORK";
            var stored = await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token);
            if (stored == null || stored.Value.Generation != generation || stored.Value.State != "DRAFT")
            { await Finish("DISCARDED", null, null); return "DISCARDED"; }
            // A concurrent edit/invalidation remains PENDING for the existing dispatcher;
            // do not publish an old candidate or consume its newer work intent.
            if (AggregateCanonical.Hash(stored) != AggregateCanonical.Hash(capture.Stored)) { await Finish("PENDING", null, null); return "NO_WORK"; }
            if (preparationError != null) {
                var state = preparationError == "AGG_INPUT_STALE" ? "PENDING" : "FAILED";
                await Finish(state, preparationError, null); return state;
            }
            if (authority == null || bundle == null) return "NO_WORK";
            var instance = stored.Value;
            // Errors after fencing/writing must abort the transaction, not be converted to success.
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(instance.Context), AggregateTargetIdentity.Id(instance.Context)!, token);
            await tx.FenceAsync(authority, bundle.Preview.Preview.LinkedSources, bundle.Preview.Preview.Coverage, token);
            var members = bundle.Recipe.Nodes.Single(n => n.Kind == "TARGET").Inputs.Select(p => p.MemberId!).ToArray();
            var next = instance with { Revision = checked(instance.Revision + 1), Context = authority.Read.Context, Applied = bundle.Preview,
                AppliedGeneration = instance.Generation, AppliedInputStamp = inputStamp,
                ComputationProfile = materialized ? AsyncProfile : instance.ComputationProfile };
            await tx.WriteTargetAsync(authority, new(next, bundle.Recipe, bundle.Preview, members), token);
            await AggregateCommandService.PutInstance(tx, next, stored.Version, token);
            await AggregateCommandService.Dependencies(tx, next, bundle.Preview, token);
            await Finish("COMPLETED", null, AggregateCanonical.Hash(bundle.Preview.Diff));
            return "COMPLETED";

            Task Finish(string state, string? error, string? diff) => tx.PutAsync(AggregateCollections.Refresh, id, intent.Version,
                intent.Value with { State = state, ErrorCode = error, DiffReference = diff, Lease = null, LeaseUntil = null, Progress = null, DispatchUntil = null },
                stored?.Value.Context.WorkId ?? "", stored == null ? null : AggregateTargetIdentity.Id(stored.Value.Context), state == "PENDING" ? [StateKey(stored?.Value,"PENDING")] : [], token);
        }, timeout.Token);
        } catch (Exception ex) {
            var recovered = await store.ExecuteAsync(async (tx, token) => {
                var intent = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
                // An unknown commit response is not proof of rollback. A durable completed
                // intent is written atomically with the target; never publish it a second time.
                if (intent?.Value.State == "COMPLETED") return "COMPLETED";
                if (intent?.Value.Lease != lease || intent.Value.State != "RUNNING") return "NO_WORK";
                var stale = ex is AggregatePreviewException { Code: "AGG_INPUT_STALE" or "AGG_REVISION_CONFLICT" };
                var transient = materialized && !ct.IsCancellationRequested && canRetryRolledBackTransaction?.Invoke(ex) == true;
                var retry = transient && intent.Value.Attempt < 5;
                var state = stale || retry ? "PENDING" : "FAILED";
                var code = transient && !retry ? "AGG_COMPUTATION_RETRY_EXHAUSTED"
                    : ex is AggregatePreviewException failure ? failure.Code : ex is OperationCanceledException ? "AGG_COMPUTATION_CANCELLED" : "AGG_COMPUTATION_FAILED";
                await tx.PutAsync(AggregateCollections.Refresh, id, intent.Version,
                    intent.Value with { State = state, ErrorCode = retry ? null : code, Lease = null, LeaseUntil = null, DispatchUntil = null, Progress = null,
                        RetryNotBefore = retry ? DateTimeOffset.UtcNow.Add(RetryDelay(intent.Value.Attempt)) : null },
                    capture.Stored?.Value.Context.WorkId ?? "", capture.Stored == null ? null : AggregateTargetIdentity.Id(capture.Stored.Value.Context), state == "PENDING" ? [StateKey(capture.Stored?.Value,"PENDING")] : [], token);
                return state;
            }, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            return recovered;
        } finally { monitorStop.Cancel(); await monitor; activeLease = null; }
    }

    internal Task ProgressAsync(string instanceId, long generation, AggregatePreviewProgress progress, CancellationToken ct)
        => store.ExecuteAsync(async (tx, token) => {
            var id = IntentKey(instanceId, generation);
            var row = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, id, token);
            if (activeLease == null || row?.Value.State != "RUNNING" || row.Value.Lease != activeLease)
                throw new OperationCanceledException("Computation no longer owns its lease.");
            var instance = await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token);
            await tx.PutAsync(AggregateCollections.Refresh, id, row.Version,
                row.Value with { Progress = progress, LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2) }, instance?.Value.Context.WorkId ?? "",
                instance == null ? null : AggregateTargetIdentity.Id(instance.Value.Context), [StateKey(instance?.Value,"RUNNING")], token);
            return true;
        }, ct);
    internal static string IntentKey(string instance, long generation) => AggregateCanonical.Key(instance, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
    internal static TimeSpan RetryDelay(int attempt)
        => TimeSpan.FromSeconds((attempt switch { 1 => 5, 2 => 15, 3 => 45, _ => 120 }) + Random.Shared.NextDouble());
    private sealed record AggregateRecipeBundle(tdtd_be.DTOs.AggregateMapping.AggregateRecipeDto Recipe, AggregatePreviewEnvelope Preview);
    private sealed record AggregateRefreshCapture(AggregateStored<AggregateRefreshIntent> Intent,
        AggregateStored<AggregateInstanceState>? Stored, tdtd_be.DTOs.AggregateMapping.AggregateRecipeDto? Recipe);
}
