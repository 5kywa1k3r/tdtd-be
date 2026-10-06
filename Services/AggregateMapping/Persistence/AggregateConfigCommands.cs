using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregatePreparedImpact(AggregateConfigImpactPlan Plan, string? Token, DateTimeOffset ExpiresAt);
internal sealed partial class AggregateCommandService
{
    internal async Task<AggregatePreparedImpact> PreviewConfigAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string configId, AggregateConfigImpactRequestDto request, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct); Allow(AggregateAction.EditConfig, authority);
        var plan = await store.ExecuteAsync((tx, token) => BuildImpact(tx, command, authority, configId, request, token), ct);
        plan = await CompleteImpact(plan, command, ct);
        var expiry = (previewCompletedAt?.Invoke() ?? command.Now).AddMinutes(5);
        var confirmation = plan.Conflicts.Count > 0 ? null : tokens.Issue(Confirmation(command, "CONFIG_REVISION", configId,
            request, ImpactEvidence(plan), expiry));
        // Historical child evidence is not a read entitlement. New previews have already
        // checked source ACL; a conflict-only plan must not serialize the old Applied data.
        return new AggregatePreparedImpact(plan with
        {
            Instances = plan.Instances.Select(i => i with { Applied = null }).ToArray()
        }, confirmation, expiry);
    }
    private async Task<AggregateConfigImpactPlan> CompleteImpact(AggregateConfigImpactPlan plan, AggregateCommandContext command, CancellationToken ct)
    {
        var previews = new Dictionary<string, AggregatePreviewEnvelope>();
        foreach (var next in plan.Instances.Where(i => plan.Authorities.ContainsKey(i.Id)))
        {
            var version = new AggregateConfigVersion(plan.ConfigId, plan.HeadRevision + 1, plan.Recipe, AggregateCanonical.Hash(plan.Recipe), command.Actor, command.Now);
            var preview = await reader.PreviewAsync(next, AggregateOverlay.Effective(version, next), plan.Authorities[next.Id], ct);
            ValidPreview(preview); previews.Add(next.Id, preview);
        }
        return plan with { Previews = previews };
    }
    internal Task<AggregateCommandResult> SaveConfigAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string configId, AggregateConfigImpactRequestDto request, string confirmation, CancellationToken ct)
    {
        AggregateConfigImpactPlan? prepared = null;
        return Execute(command, new { configId, request, confirmation }, context, AggregateAction.EditConfig, async (tx, authority, token) =>
        {
            var head = await Required<AggregateConfigHead>(tx, AggregateCollections.Configs, configId, token);
            var current = await BuildImpact(tx, command, authority, configId, request, token);
            var plan = prepared ?? throw new InvalidOperationException("AGG_PREPARATION_REQUIRED");
            if (ImpactCapture(current) != ImpactCapture(plan)) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            if (plan.Conflicts.Count != 0) throw new AggregatePreviewException("AGG_REBASE_REQUIRED");
            tokens.Verify(confirmation, Confirmation(command, "CONFIG_REVISION", configId, request, ImpactEvidence(plan), command.Now.AddMinutes(5)), command.Now);
            await tx.FenceAsync(authority, [], [], token);
            var nextRevision = checked(head.Value.HeadRevision + 1);
            await tx.PutAsync(AggregateCollections.Versions, VersionKey(configId, nextRevision), 0,
                new AggregateConfigVersion(configId, nextRevision, plan.Recipe, AggregateCanonical.Hash(plan.Recipe), command.Actor, command.Now), context.WorkId, configId, [], token);
            foreach (var next in plan.Instances)
            {
                var stored = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, next.Id, token);
                var nextAuthority = plan.Authorities[next.Id];
                Allow(AggregateAction.ApplyDraft, nextAuthority);
                Draft(stored.Value, nextAuthority, next.Revision - 1);
                await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(next.Context), AggregateTargetIdentity.Id(next.Context)!, token);
                var preview = plan.Previews[next.Id];
                var version = new AggregateConfigVersion(configId, nextRevision, plan.Recipe, AggregateCanonical.Hash(plan.Recipe), command.Actor, command.Now);
                var effective = AggregateOverlay.Effective(version, next);
                await tx.FenceAsync(nextAuthority, preview.Preview.LinkedSources, preview.Preview.Coverage, token);
                var members = effective.Nodes.Single(n => n.Kind == "TARGET").Inputs.Select(p => p.MemberId!).ToArray();
                await ClaimTargets(tx, next, members, token);
                await tx.WriteTargetAsync(nextAuthority, new(next, effective, preview, members), token);
                await PutInstance(tx, next with { Applied = preview, AppliedGeneration = next.Generation, AppliedInputStamp = null }, stored.Version, token);
                await Dependencies(tx, next, preview, token);
            }
            // Publish the head only inside the transaction containing every selected Draft write.
            await tx.PutAsync(AggregateCollections.Configs, configId, head.Version, head.Value with { HeadRevision = nextRevision, OwnerUserId = command.Actor }, context.WorkId, context.AssignmentId, [], token);
            return new(configId, nextRevision, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, "CONFIG_SAVED");
        }, ct, async (authority, token) =>
        {
            var capture = await store.ExecuteAsync((tx, cancel) => BuildImpact(tx, command, authority, configId, request, cancel), token);
            prepared = await CompleteImpact(capture, command, token);
        });
    }
    private async Task<AggregateConfigImpactPlan> BuildImpact(IAggregateTransaction tx, AggregateCommandContext command,
        AggregateCommitAuthority authority, string configId, AggregateConfigImpactRequestDto request, CancellationToken ct)
    {
        if (request.MigrateInstances.Count > 100 || request.MigrateInstances.Select(i => i.InstanceId).Distinct().Count() != request.MigrateInstances.Count)
            throw new AggregatePreviewException("AGG_MIGRATION_SELECTION_INVALID");
        if (request.Resolutions.Any(c => !request.MigrateInstances.Any(i => i.InstanceId == c.InstanceId))) throw new AggregatePreviewException("AGG_REBASE_CHOICE_INVALID");
        var head = (await Required<AggregateConfigHead>(tx, AggregateCollections.Configs, configId, ct)).Value; Scope(head, authority);
        if (head.HeadRevision != request.ExpectedHeadRevision) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        var recipe = AggregateOverlay.Validate(request.Recipe);
        if (recipe.Nodes.Single(n => n.Kind == "TARGET").Form != head.TargetForm) throw new AggregatePreviewException("AGG_SCHEMA_INCOMPATIBLE");
        var nextVersion = new AggregateConfigVersion(configId, checked(head.HeadRevision + 1), recipe, AggregateCanonical.Hash(recipe), command.Actor, command.Now);
        var instances = new List<AggregateInstanceState>(); var conflicts = new List<AggregateMigrationConflictDto>();
        var previews = new Dictionary<string, AggregatePreviewEnvelope>();
        var authorities = new Dictionary<string, AggregateCommitAuthority>();
        foreach (var pin in request.MigrateInstances.OrderBy(i => i.InstanceId, StringComparer.Ordinal))
        {
            var current = (await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, pin.InstanceId, ct)).Value;
            if (current.ConfigId != configId || current.State == "UNLINKED") throw new AggregatePreviewException("AGG_MIGRATION_SELECTION_INVALID");
            var currentAuthority = await reader.AuthorizeAsync(current.Context, command.Actor, command.SessionKey, ct);
            Allow(AggregateAction.ApplyDraft, currentAuthority); Scope(head, currentAuthority); Draft(current, currentAuthority, pin.ExpectedRevision);
            var previous = (await Required<AggregateConfigVersion>(tx, AggregateCollections.Versions, VersionKey(configId, current.ConfigRevision), ct)).Value;
            var rebased = AggregateOverlay.Rebase(current, previous, nextVersion, request.Resolutions);
            conflicts.AddRange(rebased.Conflicts);
            var next = current with { ConfigRevision = nextVersion.Revision, Overrides = rebased.Overlay,
                Revision = checked(current.Revision + 1), Generation = checked(current.Generation + 1), Context = currentAuthority.Read.Context };
            instances.Add(next);
            if (rebased.Conflicts.Count == 0)
            {
                var effective = AggregateOverlay.Effective(nextVersion, next);
                if (next.Context.View != null)
                {
                    var selected = next.Selection.Sources.ToDictionary(s => s.SourceNodeId);
                    next = next with { Selection = new(effective.Nodes.Where(n => n.Kind == "SOURCE").Select(n =>
                        selected.GetValueOrDefault(n.Id) ?? new AggregateSourceSelectionDto(n.Id, "FORM_SELECTOR", [], [])).ToList()) };
                    instances[^1] = next;
                }
                // New source nodes must be selected deliberately; a changed topology is never auto-mapped.
                if (effective.Nodes.Count(n => n.Kind == "SOURCE") != next.Selection.Sources.Count)
                    throw new AggregatePreviewException("AGG_MIGRATION_SELECTION_REPAIR_REQUIRED");
                authorities.Add(next.Id, currentAuthority);
            }
        }
        return new(configId, head.HeadRevision, recipe, instances, conflicts, previews, authorities);
    }
    private static string ImpactCapture(AggregateConfigImpactPlan plan) => AggregateCanonical.Hash(new
        { plan.ConfigId, plan.HeadRevision, plan.Recipe, plan.Instances, plan.Conflicts });
    private static string ImpactEvidence(AggregateConfigImpactPlan plan) => AggregateCanonical.Hash(new
    {
        plan.ConfigId, plan.HeadRevision, plan.Recipe, plan.Instances,
        authorities = plan.Authorities.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value.Pins }),
        previews = plan.Previews.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value.Preview.Revisions,
            p.Value.Preview.Results, p.Value.Preview.LinkedSources, p.Value.Preview.Coverage, p.Value.Preview.Windows, p.Value.SourceValues })
    });

    internal Task<AggregateCommandResult> SaveRawDraftAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string instanceId, long expectedRevision, string rawJson, CancellationToken ct)
        => Execute(command, new { instanceId, expectedRevision, rawJson }, context, AggregateAction.EditMapping, async (tx, authority, token) =>
        {
            if (System.Text.Encoding.UTF8.GetByteCount(rawJson) > 262144) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
            var stored = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, instanceId, token); Draft(stored.Value, authority, expectedRevision);
            var parsed = AggregateMappingValidator.Parse(rawJson);
            var raw = new AggregateRawDraftDto(rawJson, parsed.Issues.ToList(), stored.Value.Applied == null ? null : AggregateCanonical.Hash(stored.Value.Applied));
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(context), AggregateTargetIdentity.Id(context)!, token);
            await tx.FenceAsync(authority, [], [], token);
            var next = stored.Value with { RawDraft = raw, State = "NEEDS_REPAIR", Revision = checked(stored.Value.Revision + 1), Generation = checked(stored.Value.Generation + 1) };
            await PutInstance(tx, next, stored.Version, token);
            return new(instanceId, next.Revision, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, next.State);
        }, ct);

    internal async Task<string> PreviewDeclarationAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        AggregateDataWindowDeclarationDto declaration, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct); Allow(AggregateAction.EditMapping, authority);
        context = authority.Read.Context;
        ValidateDeclaration(declaration);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var id = DeclarationKey(context);
            var current = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, id, token);
            if ((current?.Value.Declaration.Revision ?? 0) != declaration.Revision - 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            return tokens.Issue(Confirmation(command, "DATA_WINDOW", id, declaration,
                AggregateCanonical.Hash(new { current, authority.Read.Revisions, authority.Pins }), command.Now.AddMinutes(5)));
        }, ct);
    }
    internal Task<AggregateCommandResult> SaveDeclarationAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        AggregateDataWindowDeclarationDto declaration, string confirmation, CancellationToken ct)
        => Execute(command, new { declaration, confirmation }, context, AggregateAction.EditMapping, async (tx, authority, token) =>
        {
            context = authority.Read.Context;
            if (context.View != null) return await SaveViewDeclaration(tx, authority, command, declaration, confirmation, token);
            ValidateDeclaration(declaration);
            var id = DeclarationKey(context);
            var current = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, id, token);
            if ((current?.Value.Declaration.Revision ?? 0) != declaration.Revision - 1) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            tokens.Verify(confirmation, Confirmation(command, "DATA_WINDOW", id, declaration,
                AggregateCanonical.Hash(new { current, authority.Read.Revisions, authority.Pins }), command.Now.AddMinutes(5)), command.Now);
            if (AggregateTargetIdentity.Id(context) != null) await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(context), AggregateTargetIdentity.Id(context)!, token);
            var slotId = context.BindingId + ":" + (context.Kind == "ONCE" ? "ONCE" : context.PeriodKey);
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, "SLOT", slotId, token);
            await tx.FenceAsync(authority, [], [], token);
            await tx.PutAsync(AggregateCollections.Declarations, id, current?.Version ?? 0,
                new AggregateDeclarationState(AggregateTargetIdentity.Id(context) == null ? "SLOT" : "REPORT", AggregateTargetIdentity.Id(context) ?? slotId,
                    context.WorkId, context.AssignmentId, declaration), context.WorkId, AggregateTargetIdentity.Id(context), [], token);
            if (AggregateTargetIdentity.Id(context) != null)
            {
                var slot = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, "SLOT:" + slotId, token);
                await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slotId, slot?.Version ?? 0,
                    new AggregateDeclarationState("SLOT", slotId, context.WorkId, context.AssignmentId,
                        declaration with { Revision = checked((slot?.Value.Declaration.Revision ?? 0) + 1) }), context.WorkId, AggregateTargetIdentity.Id(context), [], token);
            }
            await AggregateRefreshService.InvalidateAsync(tx, context.WorkId, [id, "SLOT:" + slotId], command.CommandId, token);
            if (AggregateTargetIdentity.Id(context) != null)
                foreach (var own in await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances, new(context.WorkId, AggregateTargetIdentity.Id(context)), token))
                    if (own.Value.State == "DRAFT") await AggregateRefreshService.EnqueueAsync(tx, own.Value, "DATA_WINDOW:" + command.CommandId, token);
            return new(AggregateTargetIdentity.Id(context) ?? slotId, declaration.Revision, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, "DATA_WINDOW_SAVED");
        }, ct);
    private static string DeclarationKey(AggregatePeriodContextDto context) => context.View != null ? "VIEW:" + context.View.ViewId : AggregateTargetIdentity.Id(context) != null ? "REPORT:" + AggregateTargetIdentity.Id(context)
        : "SLOT:" + context.BindingId + ":" + (context.Kind == "ONCE" ? "ONCE" : context.PeriodKey);
    private static void ValidateDeclaration(AggregateDataWindowDeclarationDto declaration)
    {
        if (declaration.Revision < 1 || declaration.ProvenanceKind != "USER_DECLARED" || string.IsNullOrWhiteSpace(declaration.ProvenanceRef)
            || AggregateTimeResolver.Date(declaration.StartDate) > AggregateTimeResolver.Date(declaration.EndDate))
            throw new AggregatePreviewException("AGG_DATA_WINDOW_UNRESOLVED");
    }
}
