using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowTemplateReadAccess
{
    public static bool IsAdministrator(AppUser? actor)
        => actor is not null &&
           (HasRole(actor, "ADMIN") || HasRole(actor, "SYSTEM_ADMIN"));

    /// <summary>
    /// Flow-definition creation is a design-time management capability.  Keep
    /// the allow-list server owned; a role/permission flag supplied by a
    /// request is never consulted here.
    /// </summary>
    public static bool CanCreateDefinition(AppUser? actor)
        => actor is not null &&
           (HasRole(actor, "SYSTEM_ADMIN") ||
            HasRole(actor, "ADMIN") ||
            HasRole(actor, "MANAGER_LEVEL") ||
            HasManagerUnitRoleForOwnUnit(actor) ||
            HasRole(actor, "DYNAMIC_FLOW_MANAGER"));

    public static bool HasBusinessExecuteGrant(
        WorkAssignment? assignment,
        string? templateId,
        string? actorUserId,
        int? versionNo = null)
        => IsAssignmentParticipant(assignment, templateId, actorUserId, versionNo);

    public static bool IsOwner(DynamicFlowTemplate? template, string? actorUserId)
        => template is not null &&
           !string.IsNullOrWhiteSpace(actorUserId) &&
           string.Equals(
               string.IsNullOrWhiteSpace(template.OwnerUserId)
                   ? template.CreatedByUserId
                   : template.OwnerUserId,
               actorUserId,
               StringComparison.Ordinal);

    public static bool IsAssignmentParticipant(
        WorkAssignment? assignment,
        string? templateId,
        string? actorUserId,
        int? versionNo = null)
    {
        if (assignment is null ||
            assignment.IsDeleted ||
            !assignment.IsActive ||
            string.IsNullOrWhiteSpace(templateId) ||
            string.IsNullOrWhiteSpace(actorUserId) ||
            !string.Equals(assignment.FlowTemplateId, templateId, StringComparison.Ordinal) ||
            assignment.FlowTemplateVersionNo is null ||
            !string.Equals(
                assignment.FlowEffectiveStatus,
                DynamicFlowEffectiveStatuses.Effective,
                StringComparison.Ordinal) ||
            (versionNo.HasValue && assignment.FlowTemplateVersionNo != versionNo))
        {
            return false;
        }

        return string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal) ||
               (assignment.LeaderWatcherUserIds ?? new List<string>())
                   .Contains(actorUserId, StringComparer.Ordinal) ||
               (assignment.Assignees ?? new List<UserRef>())
                   .Any(x => string.Equals(x.UserId, actorUserId, StringComparison.Ordinal));
    }

    public static FilterDefinition<WorkAssignment> BuildAssignmentParticipantFilter(
        string actorUserId,
        string? templateId = null,
        int? versionNo = null)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.IsActive, true) &
                     fb.Eq(x => x.FlowEffectiveStatus, DynamicFlowEffectiveStatuses.Effective) &
                     fb.Ne(x => x.FlowTemplateVersionNo, null) &
                     (fb.Eq(x => x.CreatedByUserId, actorUserId) |
                      fb.AnyEq(x => x.LeaderWatcherUserIds, actorUserId) |
                      fb.ElemMatch(x => x.Assignees, x => x.UserId == actorUserId));

        if (!string.IsNullOrWhiteSpace(templateId))
            filter &= fb.Eq(x => x.FlowTemplateId, templateId);

        if (versionNo.HasValue)
            filter &= fb.Eq(x => x.FlowTemplateVersionNo, versionNo.Value);

        return filter;
    }

    private static bool HasRole(AppUser actor, string role)
        => (actor.Roles ?? new List<string>())
            .Any(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));

    private static bool HasManagerUnitRoleForOwnUnit(AppUser actor)
        => !string.IsNullOrWhiteSpace(actor.UnitId) &&
           (actor.Roles ?? new List<string>())
               .Any(role =>
                   Roles.IsManagerUnit(role, out var managedUnitId) &&
                   string.Equals(managedUnitId, actor.UnitId, StringComparison.Ordinal));
}
