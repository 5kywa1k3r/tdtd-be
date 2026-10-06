using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateLifecycleTransition(string ReportId, string Operation, string FromStatus, string ToStatus,
    int ExpectedPayloadRevision, int ExpectedLifecycleRevision, int ResultLifecycleRevision, string SubmissionId,
    bool ExistingPolicyValidated, bool RequiredFieldsValidated, string? DraftAuthorityUserId = null);
internal sealed record AggregateSubmissionPreview(IReadOnlyList<AggregatePreviewEnvelope> Instances, string Token);

// Participant only. The existing report owner owns status, required fields, auto-approval,
// prior-period/recall rules and its outbox. No parallel submit/approve/return endpoint.
internal sealed class AggregateLifecycleParticipant(IAggregateTransactionStore store, IAggregateCommandReader reader,
    AggregateConfirmationTokens? tokens)
{
    internal async Task<AggregateSubmissionPreview> PreviewSubmissionAsync(AggregateCommandContext command,
        AggregatePeriodContextDto context, CancellationToken ct)
    {
        var authority = await reader.AuthorizeAsync(context, command.Actor, command.SessionKey, ct);
        AggregateCommandService.Allow(AggregateAction.Submit, authority);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var capture = await Capture(tx, authority, token);
            return new AggregateSubmissionPreview(capture.Select(x => x.Preview).ToArray(), (tokens ?? throw new AggregatePreviewException("AGG_CONFIRMATION_KEY_UNAVAILABLE")).Issue(AggregateCommandService.Confirmation(command,
                "SUBMIT", context.ReportId!, new { context.ReportId }, SubmissionEvidence(capture, authority), command.Now.AddMinutes(5))));
        }, ct);
    }
    internal async Task BeforeCommitAsync(IAggregateTransaction tx, AggregateCommandContext command,
        AggregateCommitAuthority authority, AggregateLifecycleTransition transition, string? confirmation, CancellationToken ct)
    {
        if (!transition.ExistingPolicyValidated || !transition.RequiredFieldsValidated
            || transition.ReportId != authority.Read.Context.ReportId || transition.ExpectedPayloadRevision != authority.Read.Revisions.PayloadRevision
            || transition.ExpectedLifecycleRevision != authority.Read.Revisions.LifecycleRevision
            || (transition.ResultLifecycleRevision != checked(transition.ExpectedLifecycleRevision + 1)
                && !(transition.FromStatus == "Draft" && transition.ToStatus == "Approved"
                    && transition.ResultLifecycleRevision == checked(transition.ExpectedLifecycleRevision + 2))))
            throw new AggregatePreviewException("AGG_LIFECYCLE_PARTICIPANT_INVALID");
        await EnsureUnlockedAsync(tx, "REPORT", transition.ReportId, ct);
        var instances = await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances,
            new(authority.Read.Context.WorkId, transition.ReportId), ct);
        if (transition.FromStatus == "Draft" && transition.ToStatus is "Submitted" or "Approved")
        {
            AggregateCommandService.Allow(AggregateAction.Submit, authority);
            var capture = await Capture(tx, authority, ct);
            if (capture.Count > 0)
            {
                if (confirmation == null) throw new AggregatePreviewException("AGG_CONFIRMATION_REQUIRED");
                (tokens ?? throw new AggregatePreviewException("AGG_CONFIRMATION_KEY_UNAVAILABLE")).Verify(confirmation, AggregateCommandService.Confirmation(command, "SUBMIT", transition.ReportId,
                    new { ReportId = transition.ReportId }, SubmissionEvidence(capture, authority), command.Now.AddMinutes(5)), command.Now);
            }
            foreach (var item in capture)
            {
                var instance = item.Stored.Value;
                if (instance.Applied == null || !SameResults(instance.Applied, item.Preview)) throw new AggregatePreviewException("AGG_INPUT_STALE");
                await tx.FenceAsync(authority, item.Preview.Preview.LinkedSources, item.Preview.Preview.Coverage, ct);
                // Existing auto-approval may emit submit and approval as two lifecycle revisions.
                // Owners are pinned to the submit revision, not the final approval revision.
                var submissionRevision = checked(transition.ExpectedLifecycleRevision + 1);
                var owner = new AggregateLockOwnerDto(transition.ReportId, submissionRevision,
                    transition.SubmissionId, "", "", instance.Id);
                foreach (var source in item.Preview.Preview.LinkedSources.OrderBy(s => s.ReportId, StringComparer.Ordinal))
                    await Acquire(tx, authority.Read.Context.WorkId, owner with { SourceKind = "REPORT", SourceIdentity = source.ReportId }, ["ASSIGNMENT:" + source.AssignmentId], ct);
                foreach (var slot in item.Preview.Preview.Coverage.OrderBy(s => s.SlotKey, StringComparer.Ordinal))
                    await Acquire(tx, authority.Read.Context.WorkId, owner with { SourceKind = "SLOT", SourceIdentity = slot.SlotKey }, ["BINDING:" + slot.BindingId], ct);
                var frozen = new AggregateFrozenState(transition.SubmissionId, instance.Id, transition.ReportId,
                    // Submission evidence is captured once in Preview; instance retains its
                    // recipe/selection/author/revisions, without another full draft evidence copy.
                    submissionRevision, instance with { Applied = null }, item.Preview, command.Now);
                await tx.PutAsync(AggregateCollections.Frozen, AggregateCanonical.Key(transition.SubmissionId, instance.Id), 0,
                    frozen, instance.Context.WorkId, transition.ReportId, ["SUBMISSION:" + transition.SubmissionId], ct);
                await AggregateCommandService.PutInstance(tx, instance with { State = "FROZEN", Applied = item.Preview,
                    SubmissionId = transition.SubmissionId, Revision = checked(instance.Revision + 1), Generation = checked(instance.Generation + 1) }, item.Stored.Version, ct);
            }
        }
        else if (transition.FromStatus is "Submitted" or "Approved" && transition.ToStatus == "Draft")
        {
            // Existing owner decides RETURN vs WITHDRAW and checks actor/reviewer/non-cascade policy.
            foreach (var stored in instances.Where(i => i.Value.State == "FROZEN"))
            {
                var instance = stored.Value;
                if (instance.SubmissionId != transition.SubmissionId) throw new AggregatePreviewException("AGG_SUBMISSION_STALE");
                var snapshot = (await AggregateCommandService.Required<AggregateFrozenState>(tx, AggregateCollections.Frozen,
                    AggregateCanonical.Key(transition.SubmissionId, instance.Id), ct)).Value;
                foreach (var source in snapshot.Preview.Preview.LinkedSources) await Release(tx, "REPORT", source.ReportId, transition.SubmissionId, instance.Id, ct);
                foreach (var slot in snapshot.Preview.Preview.Coverage) await Release(tx, "SLOT", slot.SlotKey, transition.SubmissionId, instance.Id, ct);
                var next = instance with { State = "DRAFT", SubmissionId = null,
                    AuthorityUserId = transition.DraftAuthorityUserId ?? instance.AuthorityUserId,
                    Revision = checked(instance.Revision + 1), Generation = checked(instance.Generation + 1) };
                await AggregateCommandService.PutInstance(tx, next, stored.Version, ct);
                await AggregateCommandService.Dependencies(tx, next, snapshot.Preview, ct);
                await AggregateRefreshService.EnqueueAsync(tx, next, "RETURN:" + transition.SubmissionId, ct);
                // Snapshot remains immutable. No child status is changed; other owners remain.
            }
        }
        else if (!((transition.FromStatus, transition.ToStatus) is ("Submitted", "Approved") or ("Approved", "Submitted")))
            throw new AggregatePreviewException("AGG_LIFECYCLE_TRANSITION_UNSUPPORTED");
        // Approve/recall retain the original frozen submission and locks.
    }
    internal static async Task EnsureUnlockedAsync(IAggregateTransaction tx, string kind, string identity, CancellationToken ct)
    {
        var key = kind + ":" + identity;
        var state = await tx.GetAsync<AggregateLockState>(AggregateCollections.Locks, key, ct);
        if (state?.Value.Owners.Count > 0) throw new AggregatePreviewException("AGG_SOURCE_LOCKED");
        // Write the guard row in the caller's transaction. This conflicts with concurrent lock
        // acquisition, including when the report/slot did not have a row before this command.
        var value = state?.Value ?? new AggregateLockState(kind, identity, []);
        await tx.PutAsync(AggregateCollections.Locks, key, state?.Version ?? 0, value, value.WorkId, identity, value.Keys ?? [], ct);
    }
    internal static async Task EnsureMutationAsync(IAggregateTransaction tx, string reportId, string bindingId,
        string occurrenceKey, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(reportId)) await EnsureUnlockedAsync(tx, "REPORT", reportId, ct);
        await EnsureUnlockedAsync(tx, "SLOT", bindingId + ":" + occurrenceKey, ct);
    }
        // Caller MUST use the same transaction for the subsequent save/version/materialize.
        // Occurrence identity excludes scheduleRevision so editing a schedule cannot bypass it.
    private static async Task Acquire(IAggregateTransaction tx, string workId, AggregateLockOwnerDto owner, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var key = owner.SourceKind + ":" + owner.SourceIdentity;
        var state = await tx.GetAsync<AggregateLockState>(AggregateCollections.Locks, key, ct);
        var owners = state?.Value.Owners.ToList() ?? [];
        if (!owners.Contains(owner)) owners.Add(owner);
        var allKeys = (state?.Value.Keys ?? []).Concat(keys).Distinct(StringComparer.Ordinal).ToArray();
        await tx.PutAsync(AggregateCollections.Locks, key, state?.Version ?? 0, new AggregateLockState(owner.SourceKind, owner.SourceIdentity, owners, workId, allKeys),
            workId, owner.SourceIdentity, allKeys, ct);
    }
    private static async Task Release(IAggregateTransaction tx, string kind, string identity, string submission, string instance, CancellationToken ct)
    {
        var key = kind + ":" + identity;
        var state = await AggregateCommandService.Required<AggregateLockState>(tx, AggregateCollections.Locks, key, ct);
        var remaining = state.Value.Owners.Where(o => o.SubmissionId != submission || o.InstanceId != instance).ToArray();
        if (remaining.Length == state.Value.Owners.Count) throw new AggregatePreviewException("AGG_LOCK_OWNER_MISSING");
        await tx.PutAsync(AggregateCollections.Locks, key, state.Version, state.Value with { Owners = remaining }, state.Value.WorkId, identity, state.Value.Keys ?? [], ct);
    }
    private async Task<List<(AggregateStored<AggregateInstanceState> Stored, AggregatePreviewEnvelope Preview)>> Capture(
        IAggregateTransaction tx, AggregateCommitAuthority authority, CancellationToken ct)
    {
        var instances = await tx.QueryAsync<AggregateInstanceState>(AggregateCollections.Instances,
            new(authority.Read.Context.WorkId, authority.Read.Context.ReportId), ct);
        var result = new List<(AggregateStored<AggregateInstanceState>, AggregatePreviewEnvelope)>();
        foreach (var stored in instances.Where(i => i.Value.State != "UNLINKED").OrderBy(i => i.Value.Id, StringComparer.Ordinal))
        {
            var instance = stored.Value; AggregateCommandService.Draft(instance, authority, instance.Revision);
            var version = (await AggregateCommandService.Required<AggregateConfigVersion>(tx, AggregateCollections.Versions,
                AggregateCommandService.VersionKey(instance.ConfigId, instance.ConfigRevision), ct)).Value;
            var intent = await tx.GetAsync<AggregateRefreshIntent>(AggregateCollections.Refresh, AggregateRefreshService.IntentKey(instance.Id, instance.Generation), ct);
            if (intent?.Value.State is "PENDING" or "RUNNING" or "FAILED" or "CANCELLED")
                throw new AggregatePreviewException("AGG_COMPUTATION_REQUIRED");
            var effective = AggregateOverlay.Effective(version, instance);
            AggregatePreviewEnvelope preview;
            if (instance.AppliedInputStamp != null)
            {
                if (instance.Applied == null || instance.AppliedGeneration != instance.Generation)
                    throw new AggregatePreviewException("AGG_COMPUTATION_REQUIRED");
                if (instance.AppliedInputStamp != await reader.InputStampAsync(instance, effective, authority, ct))
                    throw new AggregatePreviewException("AGG_INPUT_STALE");
                // Materialization is authoritative. Submit checks headers/pins/ACL and
                // freezes the saved result; no evaluator or source payload scan here.
                preview = instance.Applied with { Preview = instance.Applied.Preview with {
                    Context = authority.Read.Context,
                    Revisions = authority.Read.Revisions with { InstanceRevision = instance.Revision, ConfigRevision = instance.ConfigRevision }
                } };
            }
            else preview = await reader.PreviewAsync(instance, effective, authority, ct); // legacy persisted results remain compatible
            AggregateCommandService.ValidPreview(preview); result.Add((stored, preview));
        }
        return result;
    }
    private static bool SameResults(AggregatePreviewEnvelope a, AggregatePreviewEnvelope b)
        => AggregateCanonical.Hash(a.Preview.Results.Select(r => new { r.NodeId, r.PortId, r.State, r.Value }))
            == AggregateCanonical.Hash(b.Preview.Results.Select(r => new { r.NodeId, r.PortId, r.State, r.Value }));
    private static string SubmissionEvidence(IReadOnlyList<(AggregateStored<AggregateInstanceState> Stored, AggregatePreviewEnvelope Preview)> captures,
        AggregateCommitAuthority authority) => AggregateCanonical.Hash(captures.Select(c => AggregateCommandService.Evidence(c.Preview, authority, c.Stored.Value)));
}
