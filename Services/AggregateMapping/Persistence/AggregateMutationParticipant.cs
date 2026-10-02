namespace tdtd_be.Services.AggregateMapping.Persistence;

internal sealed record AggregateMutationBoundary(string WorkId, string AssignmentId, string? ReportId,
    string BindingId, string OccurrenceKey, string Operation, IReadOnlyList<string> ChangedMembers,
    bool ExistingPolicyValidated, string? MappingWriterInstanceId = null);

internal static class AggregateMutationParticipant
{
    internal static readonly IReadOnlySet<string> ReportOperations = new HashSet<string>(StringComparer.Ordinal)
    { "SAVE", "PATCH", "SUBMIT", "RESUBMIT", "APPROVE", "AUTO_APPROVE", "RECALL", "RETURN", "WITHDRAW",
      "DELETE", "DEACTIVATE", "REACTIVATE", "NEW_VERSION", "OPEN", "INIT", "MATERIALIZE", "REFRESH" };
    internal static async Task BeforeReportWriteAsync(IAggregateTransaction tx, AggregateMutationBoundary boundary, CancellationToken ct)
    {
        if (!boundary.ExistingPolicyValidated || !ReportOperations.Contains(boundary.Operation)
            || string.IsNullOrEmpty(boundary.BindingId) || string.IsNullOrEmpty(boundary.OccurrenceKey))
            throw new AggregatePreviewException("AGG_MUTATION_BOUNDARY_INVALID");
        await AggregateLifecycleParticipant.EnsureMutationAsync(tx, boundary.ReportId ?? "", boundary.BindingId, boundary.OccurrenceKey, ct);
        if (boundary.ReportId != null)
        {
            var claims = await tx.QueryAsync<AggregateTargetClaim>(AggregateCollections.Targets, new(boundary.WorkId, boundary.ReportId), ct);
            if (claims.Any(c => boundary.ChangedMembers.Contains(c.Value.MemberId)
                && c.Value.InstanceId != boundary.MappingWriterInstanceId)) throw new AggregatePreviewException("AGG_TARGET_MAPPED_READ_ONLY");
        }
    }
    internal static Task AfterReportWriteAsync(IAggregateTransaction tx, AggregateMutationBoundary boundary, string eventId, CancellationToken ct)
        => AggregateRefreshService.InvalidateAsync(tx, boundary.WorkId,
            (boundary.ReportId == null ? Array.Empty<string>() : new[] { "REPORT:" + boundary.ReportId })
            .Concat(["SLOT:" + boundary.BindingId + ":" + boundary.OccurrenceKey]).ToArray(), eventId, ct);

    // Invoke in the tree owner's transaction, with every affected canonical identity from
    // the before/after tree. Revisions never become part of the slot identity.
    internal static async Task BeforeRelationshipWriteAsync(IAggregateTransaction tx, string workId,
        IReadOnlyList<string> affectedAssignmentIds, IReadOnlyList<string> affectedBindingIds, bool existingPolicyValidated, CancellationToken ct)
    {
        if (!existingPolicyValidated) throw new AggregatePreviewException("AGG_MUTATION_BOUNDARY_INVALID");
        foreach (var key in affectedAssignmentIds.Select(id => "ASSIGNMENT:" + id).Concat(affectedBindingIds.Select(id => "BINDING:" + id)).Distinct(StringComparer.Ordinal))
            foreach (var item in await tx.QueryAsync<AggregateLockState>(AggregateCollections.Locks, new(workId, DependencyKey: key), ct))
                await AggregateLifecycleParticipant.EnsureUnlockedAsync(tx, item.Value.Kind, item.Value.Identity, ct);
    }
    internal static void EnsureParentCompletion(bool existingPeriodsReady, IReadOnlyList<bool> directChildrenComplete)
    {
        if (!existingPeriodsReady || directChildrenComplete.Any(complete => !complete))
            throw new AggregatePreviewException("AGG_ASSIGNMENT_CHILDREN_PENDING");
    }
}
