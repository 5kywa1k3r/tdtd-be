using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateQueueConfirmation(string Token, DateTimeOffset ExpiresAt);

// Saving a method is independent of evaluating its sources. Only a durable, authorized
// intent may materialize a report; free previews never acquire write authority.
internal sealed partial class AggregateCommandService
{
    private static string QueueEvidence(AggregateCommitAuthority authority, object capture)
        => AggregateCanonical.Hash(new { capture, authority.Read.Revisions, authority.Read.AuthorizationFingerprint, authority.Pins });
    // Every instance write advances the durable CAS version/revision. Its prior
    // derived result is irrelevant to authorization for saving a method, and
    // must not be serialized again into the metadata confirmation hash.
    private static AggregateStored<AggregateInstanceState> QueueCapture(AggregateStored<AggregateInstanceState> stored)
        => stored with { Value = stored.Value with { Applied = null } };
    private static string QueueImpactEvidence(AggregateConfigImpactPlan plan)
        => ImpactEvidence(plan with { Instances = plan.Instances.Select(i => i with { Applied = null }).ToArray() });

    internal async Task<AggregateQueueConfirmation> PreviewQueuedMappingAsync(AggregateCommandContext command,
        AggregatePeriodContextDto context, string id, AggregateMappingChange change, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        Allow(AggregateAction.ApplyDraft, authority);
        var capture = await store.ExecuteAsync((tx, token) => CaptureChange(tx, authority, id, change, token), ct);
        var expiry = command.Now.AddMinutes(5);
        return new(tokens.Issue(Confirmation(command, "QUEUE_MAPPING", id, change,
            QueueEvidence(authority, QueueCapture(capture.Stored)), expiry)), expiry);
    }

    internal Task<AggregateCommandResult> SaveQueuedMappingAsync(AggregateCommandContext command,
        AggregatePeriodContextDto context, string id, AggregateMappingChange change, string confirmation, CancellationToken ct)
        => Execute(command, new { id, change, confirmation }, context, AggregateAction.ApplyDraft, async (tx, authority, token) =>
        {
            var capture = await CaptureChange(tx, authority, id, change, token);
            tokens.Verify(confirmation, Confirmation(command, "QUEUE_MAPPING", id, change,
                QueueEvidence(authority, QueueCapture(capture.Stored)), command.Now.AddMinutes(5)), command.Now);
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(context), AggregateTargetIdentity.Id(context)!, token);
            await tx.FenceAsync(authority, [], [], token);
            var next = capture.Next with { Revision = checked(capture.Stored.Value.Revision + 1),
                Generation = checked(capture.Stored.Value.Generation + 1), AuthorityUserId = command.Actor, ComputationProfile = AggregateRefreshService.AsyncProfile };
            await ClaimTargets(tx, next, ManagedMembers(capture.Recipe), token);
            await PutInstance(tx, next, capture.Stored.Version, token);
            await PendingDependencies(tx, next, token);
            await AggregateRefreshService.EnqueueAsync(tx, next, "SAVE:" + command.CommandId, token);
            return new(id, next.Revision, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, "QUEUED");
        }, ct);

    internal async Task<AggregateQueueConfirmation> PreviewQueuedConfigAsync(AggregateCommandContext command,
        AggregatePeriodContextDto context, string id, AggregateConfigImpactRequestDto request, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        Allow(AggregateAction.EditConfig, authority);
        var plan = await store.ExecuteAsync((tx, token) => BuildImpact(tx, command, authority, id, request, token), ct);
        if (plan.Conflicts.Count != 0) throw new AggregatePreviewException("AGG_REBASE_REQUIRED");
        var expiry = command.Now.AddMinutes(5);
        return new(tokens.Issue(Confirmation(command, "QUEUE_CONFIG", id, request,
            QueueEvidence(authority, QueueImpactEvidence(plan)), expiry)), expiry);
    }

    internal Task<AggregateCommandResult> SaveQueuedConfigAsync(AggregateCommandContext command,
        AggregatePeriodContextDto context, string id, AggregateConfigImpactRequestDto request, string confirmation, CancellationToken ct)
        => Execute(command, new { id, request, confirmation }, context, AggregateAction.EditConfig, async (tx, authority, token) =>
        {
            var plan = await BuildImpact(tx, command, authority, id, request, token);
            if (plan.Conflicts.Count != 0) throw new AggregatePreviewException("AGG_REBASE_REQUIRED");
            tokens.Verify(confirmation, Confirmation(command, "QUEUE_CONFIG", id, request,
                QueueEvidence(authority, QueueImpactEvidence(plan)), command.Now.AddMinutes(5)), command.Now);
            var head = await Required<AggregateConfigHead>(tx, AggregateCollections.Configs, id, token);
            var revision = checked(head.Value.HeadRevision + 1);
            var version = new AggregateConfigVersion(id, revision, plan.Recipe, AggregateCanonical.Hash(plan.Recipe), command.Actor, command.Now);
            await tx.FenceAsync(authority, [], [], token);
            await tx.PutAsync(AggregateCollections.Versions, VersionKey(id, revision), 0, version, context.WorkId, id, [], token);
            foreach (var candidate in plan.Instances)
            {
                var stored = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, candidate.Id, token);
                var currentAuthority = plan.Authorities[candidate.Id];
                await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(candidate.Context), AggregateTargetIdentity.Id(candidate.Context)!, token);
                await tx.FenceAsync(currentAuthority, [], [], token);
                var next = candidate with { AuthorityUserId = command.Actor, ComputationProfile = AggregateRefreshService.AsyncProfile };
                await ClaimTargets(tx, next, ManagedMembers(AggregateOverlay.Effective(version, next)), token);
                await PutInstance(tx, next, stored.Version, token);
                await PendingDependencies(tx, next, token);
                await AggregateRefreshService.EnqueueAsync(tx, next, "CONFIG:" + command.CommandId, token);
            }
            await tx.PutAsync(AggregateCollections.Configs, id, head.Version, head.Value with { HeadRevision = revision, OwnerUserId = command.Actor }, context.WorkId, context.AssignmentId, [], token);
            return new(id, revision, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, plan.Instances.Count == 0 ? "CONFIG_SAVED" : "QUEUED");
        }, ct);

    private static string[] ManagedMembers(AggregateRecipeDto recipe)
        => recipe.Nodes.Single(n => n.Kind == "TARGET").Inputs.Select(p => p.MemberId!).Distinct(StringComparer.Ordinal).ToArray();

    internal async Task<AggregateCommandResult> ChangeComputationAsync(AggregateCommandContext command,
        AggregatePeriodContextDto context, string id, long expectedRevision, bool retry, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        Allow(AggregateAction.EditMapping, authority);
        return await store.ExecuteAsync(async (tx, token) => {
            var stored = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, id, token);
            Draft(stored.Value, authority, expectedRevision);
            await tx.FenceAsync(authority, [], [], token);
            if (retry) await AggregateRefreshService.EnqueueAsync(tx, stored.Value with { AuthorityUserId = command.Actor }, "RETRY:" + Guid.NewGuid().ToString("N"), token);
            else {
                var key = AggregateRefreshService.IntentKey(id, stored.Value.Generation);
                var intent = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, key, token);
                if (intent?.Value.State is "PENDING" or "RUNNING")
                    await tx.PutAsync(AggregateCollections.Refresh, key, intent.Version,
                        intent.Value with { State = "CANCELLED", Lease = null, LeaseUntil = null, DispatchUntil = null, Progress = null },
                        context.WorkId, AggregateTargetIdentity.Id(context), [], token);
            }
            return new AggregateCommandResult(id, stored.Value.Revision, authority.Read.Revisions.PayloadRevision,
                authority.Read.Revisions.LifecycleRevision, retry ? "QUEUED" : "CANCELLED");
        }, ct);
    }

    private static async Task PendingDependencies(IAggregateTransaction tx, AggregateInstanceState instance, CancellationToken ct)
    {
        var old = await tx.GetAsync<AggregateDependencyState>(AggregateCollections.Dependencies, instance.Id, ct);
        var keys = (old?.Value.Keys ?? []).Concat(["MEMBERSHIP:" + instance.Context.AssignmentId, "CONFIG:" + instance.ConfigId])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var value = new AggregateDependencyState(instance.Id, AggregateTargetIdentity.Id(instance.Context)!, instance.Generation,
            keys, old?.Value.Reports ?? [], old?.Value.Slots ?? []);
        await tx.PutAsync(AggregateCollections.Dependencies, instance.Id, old?.Version ?? 0, value,
            instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), keys, ct);
    }
}
