using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateMappingChange(long ExpectedRevision, AggregateInstanceOverrideDto? Overrides,
    AggregateInstanceSelectionDto Selection, IReadOnlyList<string> UnlinkedMembers, bool ResetToPinned);
internal sealed record AggregatePreparedChange(AggregateInstanceState Instance, AggregateRecipeDto Recipe,
    AggregatePreviewEnvelope Preview, string Token, DateTimeOffset ExpiresAt);
internal sealed record AggregateTargetClaim(string InstanceId, string ReportId, string MemberId);

internal sealed partial class AggregateCommandService(IAggregateTransactionStore store, IAggregateCommandReader reader,
    AggregateConfirmationTokens tokens, Func<DateTimeOffset>? previewCompletedAt = null)
{
    internal Task<AggregateSubmissionPreview> PreviewSubmissionAsync(AggregateCommandContext command, AggregatePeriodContextDto context, CancellationToken ct)
        => new AggregateLifecycleParticipant(store, reader, tokens).PreviewSubmissionAsync(command, context, ct);
    internal Task<AggregateCommandResult> CreateConfigAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        AggregateConfigCreateRequestDto request, CancellationToken ct)
        => Execute(command, request, context, AggregateAction.EditConfig, async (tx, authority, token) =>
        {
            var recipe = AggregateOverlay.Validate(request.Recipe);
            if (request.TargetBindingId != authority.Read.Context.BindingId || request.TargetForm != authority.Read.TargetSchema.Pin
                || recipe.Nodes.Single(n => n.Kind == "TARGET").Form != request.TargetForm) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
            var id = AggregateCanonical.Key(context.WorkId, context.AssignmentId, request.TargetBindingId);
            if (await tx.GetAsync<AggregateConfigHead>(AggregateCollections.Configs, id, token) != null) throw new AggregatePreviewException("AGG_CONFIG_EXISTS");
            await tx.FenceAsync(authority, [], [], token);
            var head = new AggregateConfigHead(id, context.WorkId, context.AssignmentId, request.TargetBindingId, command.Actor, request.TargetForm, 1);
            await tx.PutAsync(AggregateCollections.Configs, id, 0, head, context.WorkId, context.AssignmentId, [], token);
            await tx.PutAsync(AggregateCollections.Versions, VersionKey(id, 1), 0,
                new AggregateConfigVersion(id, 1, recipe, AggregateCanonical.Hash(recipe), command.Actor, command.Now), context.WorkId, id, [], token);
            return new(id, 1, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, "CONFIG_SAVED");
        }, ct);

    internal Task<AggregateCommandResult> CreateInstanceAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string configId, CancellationToken ct)
        => Execute(command, new { context, configId }, context, AggregateAction.EditMapping, async (tx, authority, token) =>
        {
            if (AggregateTargetIdentity.Id(authority.Read.Context) == null) throw new AggregatePreviewException("AGG_REPORT_REQUIRED");
            var head = (await Required<AggregateConfigHead>(tx, AggregateCollections.Configs, configId, token)).Value;
            Scope(head, authority);
            var id = InstanceKey(context);
            var existing = await tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, id, token);
            if (existing != null) throw new AggregatePreviewException("AGG_INSTANCE_EXISTS");
            var version = (await Required<AggregateConfigVersion>(tx, AggregateCollections.Versions, VersionKey(configId, head.HeadRevision), token)).Value;
            var selection = new AggregateInstanceSelectionDto(version.Recipe.Nodes.Where(n => n.Kind == "SOURCE")
                .Select(n => new AggregateSourceSelectionDto(n.Id, "FORM_SELECTOR", [], [])).ToList());
            var instance = new AggregateInstanceState(id, authority.Read.Context, configId, head.HeadRevision, 1, 1, "DRAFT", null,
                selection, [], null, null, null, command.Actor);
            await tx.FenceAsync(authority, [], [], token);
            await PutInstance(tx, instance, 0, token);
            return new(id, 1, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, "DRAFT");
        }, ct);

    internal async Task<AggregatePreparedChange> PreviewChangeAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string instanceId, AggregateMappingChange change, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        Allow(AggregateAction.EditMapping, authority);
        // Capture the mapping in a short read transaction; background preview must not
        // hold a Mongo transaction while reading all source payloads. Apply still fences.
        var capture = await store.ExecuteAsync((tx, token) => CaptureChange(tx, authority, instanceId, change, token), ct);
        return await CompleteChange(authority, command, instanceId, change, capture, ct);
    }
    internal Task<AggregateCommandResult> ApplyAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string instanceId, AggregateMappingChange change, string confirmation, CancellationToken ct)
    {
        AggregatePreparedChange? prepared = null;
        string? capturedInstance = null;
        return Execute(command, new { instanceId, change, confirmation }, context, AggregateAction.ApplyDraft, async (tx, authority, token) =>
        {
            var old = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, instanceId, token);
            Draft(old.Value, authority, change.ExpectedRevision);
            if (AggregateCanonical.Hash(old) != capturedInstance) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
            var candidate = prepared ?? throw new InvalidOperationException("AGG_PREPARATION_REQUIRED");
            var binding = Confirmation(command, "APPLY", instanceId, change, Evidence(candidate.Preview, authority, old.Value), candidate.ExpiresAt);
            tokens.Verify(confirmation, binding, command.Now);
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(context), AggregateTargetIdentity.Id(context)!, token);
            await tx.FenceAsync(authority, candidate.Preview.Preview.LinkedSources, candidate.Preview.Preview.Coverage, token);
            var members = candidate.Recipe.Nodes.Single(n => n.Kind == "TARGET").Inputs.Select(p => p.MemberId!).ToArray();
            await ClaimTargets(tx, old.Value, members, token);
            var next = candidate.Instance with { Revision = checked(old.Value.Revision + 1), Generation = checked(old.Value.Generation + 1), Applied = candidate.Preview, RawDraft = null, State = "DRAFT", AppliedGeneration = checked(old.Value.Generation + 1), AppliedInputStamp = null };
            var revision = await tx.WriteTargetAsync(authority, new(next, candidate.Recipe, candidate.Preview, members), token);
            await PutInstance(tx, next, old.Version, token);
            await Dependencies(tx, next, candidate.Preview, token);
            return new(instanceId, next.Revision, revision, authority.Read.Revisions.LifecycleRevision, next.State);
        }, ct, async (authority, token) =>
        {
            // Immutable artifacts may acquire/release a scope gate outside Mongo's
            // session. Finish that work before the publication snapshot begins.
            var capture = await store.ExecuteAsync((tx, cancel) => CaptureChange(tx, authority, instanceId, change, cancel), token);
            capturedInstance = AggregateCanonical.Hash(capture.Stored);
            prepared = await CompleteChange(authority, command, instanceId, change, capture, token);
        });
    }

    internal Task<AggregateCommandResult> UnlinkAllAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string instanceId, long expectedRevision, string confirmation, CancellationToken ct)
        => Execute(command, new { instanceId, expectedRevision, confirmation }, context, AggregateAction.EditMapping, async (tx, authority, token) =>
        {
            var stored = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, instanceId, token);
            var instance = stored.Value; Draft(instance, authority, expectedRevision);
            tokens.Verify(confirmation, Confirmation(command, "UNLINK", instanceId, new { expectedRevision },
                AggregateCanonical.Hash(new { instance, authority.Read.Revisions, authority.Pins }), command.Now.AddMinutes(5)), command.Now);
            await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, AggregateTargetIdentity.LockKind(context), AggregateTargetIdentity.Id(context)!, token);
            await tx.FenceAsync(authority, [], [], token);
            await ClaimTargets(tx, instance, [], token);
            var dependency = await tx.GetAsync<AggregateDependencyState>(AggregateCollections.Dependencies, instance.Id, token);
            if (dependency != null) await tx.DeleteAsync(AggregateCollections.Dependencies, instance.Id, dependency.Version, token);
            var next = instance with { State = "UNLINKED", Revision = checked(instance.Revision + 1), Generation = checked(instance.Generation + 1) };
            await PutInstance(tx, next, stored.Version, token);
            // Deliberately no payload write: existing numbers become manual, including repair data.
            return new(instanceId, next.Revision, authority.Read.Revisions.PayloadRevision, authority.Read.Revisions.LifecycleRevision, next.State);
        }, ct);

    internal async Task<string> PreviewUnlinkAsync(AggregateCommandContext command, AggregatePeriodContextDto context,
        string instanceId, long expectedRevision, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct); Allow(AggregateAction.EditMapping, authority);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var instance = (await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, instanceId, token)).Value;
            Draft(instance, authority, expectedRevision);
            return tokens.Issue(Confirmation(command, "UNLINK", instanceId, new { expectedRevision },
                AggregateCanonical.Hash(new { instance, authority.Read.Revisions, authority.Pins }), command.Now.AddMinutes(5)));
        }, ct);
    }
    private async Task<(AggregateStored<AggregateInstanceState> Stored, AggregateInstanceState Next, AggregateRecipeDto Recipe)> CaptureChange(
        IAggregateTransaction tx, AggregateCommitAuthority authority, string id, AggregateMappingChange change, CancellationToken ct)
    {
        var stored = await Required<AggregateInstanceState>(tx, AggregateCollections.Instances, id, ct);
        Draft(stored.Value, authority, change.ExpectedRevision);
        var head = (await Required<AggregateConfigHead>(tx, AggregateCollections.Configs, stored.Value.ConfigId, ct)).Value; Scope(head, authority);
        var version = (await Required<AggregateConfigVersion>(tx, AggregateCollections.Versions, VersionKey(head.Id, stored.Value.ConfigRevision), ct)).Value;
        var (next, recipe) = MappingCandidate(stored.Value, version, change, authority.Read.Context);
        return (stored, next, recipe);
    }
    // Preparing a repair is read-only. The stored raw draft remains part of confirmation
    // evidence and is cleared only by the fenced Apply transaction.
    internal static (AggregateInstanceState Next, AggregateRecipeDto Recipe) MappingCandidate(
        AggregateInstanceState instance, AggregateConfigVersion version, AggregateMappingChange change, AggregatePeriodContextDto context)
    {
        var next = instance with { Overrides = change.ResetToPinned ? null : change.Overrides, Selection = change.Selection,
            UnlinkedMembers = change.ResetToPinned ? [] : change.UnlinkedMembers, RawDraft = null, State = "DRAFT", Context = context };
        if (change.ResetToPinned)
            next = next with { Selection = new(version.Recipe.Nodes.Where(n => n.Kind == "SOURCE").Select(n => new AggregateSourceSelectionDto(n.Id, "FORM_SELECTOR", [], [])).ToList()) };
        var recipe = AggregateOverlay.Effective(version, next);
        var sourceIds = recipe.Nodes.Where(n => n.Kind == "SOURCE").Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        next = next with { Selection = new(next.Selection.Sources.Where(s => sourceIds.Contains(s.SourceNodeId)).ToList()) };
        return (next, recipe);
    }
    private async Task<AggregatePreparedChange> CompleteChange(AggregateCommitAuthority authority, AggregateCommandContext command,
        string id, AggregateMappingChange change, (AggregateStored<AggregateInstanceState> Stored, AggregateInstanceState Next, AggregateRecipeDto Recipe) capture, CancellationToken ct)
    {
        var (stored, next, recipe) = capture;
        var preview = await reader.PreviewAsync(next, recipe, authority, ct);
        ValidPreview(preview);
        var expiry = (previewCompletedAt?.Invoke() ?? command.Now).AddMinutes(5);
        var signed = tokens.Issue(Confirmation(command, "APPLY", id, change, Evidence(preview, authority, stored.Value), expiry));
        preview = preview with { Preview = preview.Preview with
        {
            PreviewToken = signed, ExpiresAtUtc = expiry.ToString("O"),
            Capabilities = preview.Preview.Capabilities with { ApplyDraft = AggregateMappingPolicy.Decide(AggregateAction.ApplyDraft, authority.Read.Authority) }
        } };
        return new(next with { Applied = null }, recipe, preview, signed, expiry);
    }
    private async Task<AggregateCommandResult> Execute<T>(AggregateCommandContext command, T request, AggregatePeriodContextDto context,
        AggregateAction action, Func<IAggregateTransaction, AggregateCommitAuthority, CancellationToken, Task<AggregateCommandResult>> operation,
        CancellationToken ct, Func<AggregateCommitAuthority, CancellationToken, Task>? beforeTransaction = null)
    {
        AggregateCanonical.CommandId(command.CommandId);
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        // A replay still requires current read authority. Mutation checks run only for a new command.
        if (!authority.Read.Authority.ContextReadable) throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        var key = AggregateCanonical.Key(command.Actor, command.ContextId, command.Operation, command.CommandId);
        // Replays must still belong to the currently authorized report or missing occurrence.
        // A caller-controlled route key alone cannot bind the receipt to that authority.
        var hash = AggregateCanonical.Hash(new
        {
            Request = request,
            ContextIdentity = InstanceKey(authority.Read.Context),
            Occurrence = authority.Read.Context.Kind == "ONCE" ? "ONCE" : authority.Read.Context.PeriodKey
        });
        if (beforeTransaction != null)
        {
            // A lost-response replay must not recalculate or stage new artifacts.
            // The receipt is checked again in the publication transaction below.
            var receipt = await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateReceipt>(AggregateCollections.Receipts, key, token), ct);
            if (receipt != null)
            {
                if (receipt.Value.RequestHash != hash) throw new AggregatePreviewException("AGG_COMMAND_PAYLOAD_MISMATCH");
                return receipt.Value.Result.Deserialize<AggregateCommandResult>(AggregateCanonical.Json)! with { Replayed = true };
            }
            Allow(action, authority);
            await beforeTransaction(authority, ct);
        }
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var receipt = await tx.GetAsync<AggregateReceipt>(AggregateCollections.Receipts, key, token);
            if (receipt != null)
            {
                if (receipt.Value.RequestHash != hash) throw new AggregatePreviewException("AGG_COMMAND_PAYLOAD_MISMATCH");
                return receipt.Value.Result.Deserialize<AggregateCommandResult>(AggregateCanonical.Json)! with { Replayed = true };
            }
            Allow(action, authority);
            var result = await operation(tx, authority, token);
            await tx.PutAsync(AggregateCollections.Receipts, key, 0,
                new AggregateReceipt(hash, command.Actor, command.Operation, JsonSerializer.SerializeToElement(result, AggregateCanonical.Json)), context.WorkId, command.ContextId, [], token);
            return result;
        }, ct);
    }
    internal static void Allow(AggregateAction action, AggregateCommitAuthority authority)
    { var decision = AggregateMappingPolicy.Decide(action, authority.Read.Authority); if (!decision.Allowed) throw new AggregatePreviewException(decision.ReasonCode!); }
    internal static void Scope(AggregateConfigHead head, AggregateCommitAuthority authority)
    {
        var context = authority.Read.Context;
        if (head.WorkId != context.WorkId || head.AssignmentId != context.AssignmentId || head.BindingId != context.BindingId
            || head.TargetForm != authority.Read.TargetSchema.Pin) throw new AggregatePreviewException("AGG_CONFIG_UNAVAILABLE");
        // Current binding authority wins after handover; historical authorship is not rewritten.
        if (context.View == null && (!authority.Read.Authority.IsAssignmentAssignee || !authority.Read.Authority.OwnerScopeMatches)) throw new AggregatePreviewException("AGG_CONFIG_EDIT_FORBIDDEN");
    }
    internal static void Draft(AggregateInstanceState instance, AggregateCommitAuthority authority, long expected)
    {
        var context = authority.Read.Context;
        if (InstanceKey(instance.Context) != InstanceKey(context)) throw new AggregatePreviewException("AGG_CONTEXT_STALE");
        if (instance.Revision != expected) throw new AggregatePreviewException("AGG_REVISION_CONFLICT");
        if (instance.State == "FROZEN" || authority.Read.Authority.TargetStatus != "Draft") throw new AggregatePreviewException("AGG_TARGET_NOT_DRAFT");
    }
    internal static void ValidPreview(AggregatePreviewEnvelope preview)
    {
        if (preview.Preview.State != "VALID" || preview.Preview.LockImpact.State != "RESOLVED"
            || preview.Preview.Results.Any(r => r.State is not ("RESULT" or "NO_RESULT"))) throw new AggregatePreviewException("AGG_PREVIEW_UNAVAILABLE");
    }
    internal static string Evidence(AggregatePreviewEnvelope preview, AggregateCommitAuthority authority, AggregateInstanceState instance)
        => AggregateCanonical.Hash(new { preview.Preview.Revisions, preview.Preview.Results, preview.Preview.LinkedSources,
            preview.Preview.Coverage, preview.Preview.Windows, preview.SourceValues, authority.Pins, instance.Revision, instance.Generation });
    internal static AggregateConfirmation Confirmation<T>(AggregateCommandContext command, string operation, string contextId, T request, string evidence, DateTimeOffset expiry)
        => new(operation, command.Actor, command.SessionKey, contextId, AggregateCanonical.Hash(request), evidence, expiry);
    internal static string InstanceKey(AggregatePeriodContextDto context)
        => AggregateCanonical.Key(context.WorkId, context.AssignmentId, context.BindingId, AggregateTargetIdentity.Id(context) ?? "", context.WorkReportPeriodId ?? "", context.PeriodInstanceKey ?? "");
    internal static string VersionKey(string config, long revision) => AggregateCanonical.Key(config, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
    internal static async Task<AggregateStored<T>> Required<T>(IAggregateTransaction tx, string collection, string id, CancellationToken ct)
        => await tx.GetAsync<T>(collection, id, ct) ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
    internal static Task PutInstance(IAggregateTransaction tx, AggregateInstanceState next, long version, CancellationToken ct)
        => tx.PutAsync(AggregateCollections.Instances, next.Id, version, next, next.Context.WorkId, AggregateTargetIdentity.Id(next.Context), ["CONFIG:" + next.ConfigId], ct);
    internal static async Task ClaimTargets(IAggregateTransaction tx, AggregateInstanceState instance, IReadOnlyList<string> members, CancellationToken ct)
    {
        var current = await tx.QueryAsync<AggregateTargetClaim>(AggregateCollections.Targets, new(instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context)), ct);
        foreach (var claim in current.Where(c => c.Value.InstanceId == instance.Id && !members.Contains(c.Value.MemberId)))
            await tx.DeleteAsync(AggregateCollections.Targets, AggregateCanonical.Key(claim.Value.ReportId, claim.Value.MemberId), claim.Version, ct);
        foreach (var member in members)
        {
            var existing = current.SingleOrDefault(c => c.Value.MemberId == member);
            if (existing != null && existing.Value.InstanceId != instance.Id) throw new AggregatePreviewException("AGG_TARGET_ALREADY_MAPPED");
            if (existing == null) await tx.PutAsync(AggregateCollections.Targets, AggregateCanonical.Key(AggregateTargetIdentity.Id(instance.Context)!, member), 0,
                new AggregateTargetClaim(instance.Id, AggregateTargetIdentity.Id(instance.Context)!, member), instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), [], ct);
        }
    }
    internal static async Task Dependencies(IAggregateTransaction tx, AggregateInstanceState instance, AggregatePreviewEnvelope preview, CancellationToken ct)
    {
        var old = await tx.GetAsync<AggregateDependencyState>(AggregateCollections.Dependencies, instance.Id, ct);
        var keys = preview.Preview.LinkedSources.Select(s => "REPORT:" + s.ReportId)
            .Concat(preview.Preview.LinkedSources.Select(s => "ASSIGNMENT:" + s.AssignmentId))
            .Concat(preview.Preview.Coverage.Select(s => "BINDING:" + s.BindingId))
            .Concat(preview.Preview.Coverage.Select(s => "SLOT:" + s.SlotKey))
            .Concat(["MEMBERSHIP:" + instance.Context.AssignmentId, "CONFIG:" + instance.ConfigId]);
        var value = new AggregateDependencyState(instance.Id, AggregateTargetIdentity.Id(instance.Context)!, instance.Generation,
            keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray(), preview.Preview.LinkedSources, preview.Preview.Coverage);
        await tx.PutAsync(AggregateCollections.Dependencies, instance.Id, old?.Version ?? 0, value,
            instance.Context.WorkId, AggregateTargetIdentity.Id(instance.Context), value.Keys, ct);
    }
}
