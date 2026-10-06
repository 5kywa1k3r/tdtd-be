using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// Trusted application facts, NOT a request DTO. A future adapter must resolve each fact
// from current server state. A passed decision never replaces commit-time fencing.
internal sealed record AggregateAuthorityFacts(
    bool Authenticated, bool ContextReadable, bool WholeSourceReadable,
    bool SameWork, bool DirectChild, bool IntermediateSameContext, bool CanEditReport, bool CanSubmitReport,
    bool CanReviewReport, bool CanReadLineage, bool MutationScopeOpen,
    bool TargetActive, string TargetStatus, bool TargetLockedByConsumer,
    bool ConfigReadable, bool IsAssignmentAssignee, bool OwnerScopeMatches, bool DelegationValid)
{
    // A selected reopened period does not grant edits to a configuration shared by all periods.
    public bool? ConfigMutationScopeOpen { get; init; }
}

internal enum AggregateAction
{
    ReadConfig, EditConfig, EditMapping, ReadSource, Preview, ApplyDraft,
    Submit, Approve, Return, ReadLineage, RefreshDraft
}

internal static class AggregateMappingPolicy
{
    internal static AggregateCapabilityDto Decide(AggregateAction action, AggregateAuthorityFacts facts)
    {
        static AggregateCapabilityDto Deny(string reason) => new(false, reason);
        if (!facts.Authenticated) return Deny("AGG_AUTH_REQUIRED");
        if (!facts.ContextReadable) return Deny("AGG_CONTEXT_UNAVAILABLE");
        if (action == AggregateAction.ReadSource)
            return facts.SameWork && (facts.DirectChild || facts.IntermediateSameContext) && facts.WholeSourceReadable
                ? new(true, null) : Deny("AGG_SOURCE_UNAVAILABLE");
        if (action == AggregateAction.ReadConfig)
            return facts.ConfigReadable ? new(true, null) : Deny("AGG_CONFIG_UNAVAILABLE");
        if (action == AggregateAction.ReadLineage)
            return facts.SameWork && facts.CanReadLineage && facts.WholeSourceReadable
                ? new(true, null) : Deny("AGG_SOURCE_UNAVAILABLE");
        if (action == AggregateAction.EditConfig)
            return facts.IsAssignmentAssignee && facts.OwnerScopeMatches && facts.TargetActive &&
                (facts.ConfigMutationScopeOpen ?? facts.MutationScopeOpen)
                ? new(true, null) : Deny("AGG_CONFIG_EDIT_FORBIDDEN");
        if (!facts.TargetActive || !facts.MutationScopeOpen)
            return Deny("AGG_MUTATION_SCOPE_CLOSED");
        if (facts.TargetLockedByConsumer) return Deny("AGG_SOURCE_LOCKED");
        if (action == AggregateAction.Approve || action == AggregateAction.Return)
            return facts.CanReviewReport && (facts.TargetStatus == "Submitted" || action == AggregateAction.Return && facts.TargetStatus == "Approved")
                ? new(true, null) : Deny("AGG_REVIEW_FORBIDDEN");
        if (facts.TargetStatus != "Draft") return Deny("AGG_TARGET_NOT_DRAFT");
        if (action == AggregateAction.Submit)
            return facts.CanSubmitReport ? new(true, null) : Deny("AGG_SUBMIT_FORBIDDEN");
        if (action == AggregateAction.EditMapping)
            return facts.CanEditReport ? new(true, null) : Deny("AGG_MAPPING_EDIT_FORBIDDEN");
        if (action == AggregateAction.Preview || action == AggregateAction.ApplyDraft || action == AggregateAction.RefreshDraft)
        {
            if (!facts.CanEditReport) return Deny("AGG_MAPPING_EDIT_FORBIDDEN");
            if (!facts.ConfigReadable) return Deny("AGG_CONFIG_UNAVAILABLE");
            if (!facts.SameWork || !(facts.DirectChild || facts.IntermediateSameContext) || !facts.WholeSourceReadable)
                return Deny("AGG_SOURCE_UNAVAILABLE");
            if (action == AggregateAction.RefreshDraft && !facts.DelegationValid)
                return Deny("AGG_DELEGATION_REVOKED");
            return new(true, null);
        }
        return Deny("AGG_ACTION_UNSUPPORTED");
    }
}
