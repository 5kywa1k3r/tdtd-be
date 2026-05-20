using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentCreateScopeGuard
{
    public static void EnsureCanCreateWithinScope(
        Work work,
        WorkAssignment? parent,
        string actorUserId,
        IEnumerable<string>? assigneeUserIds)
    {
        EnsureActor(actorUserId);

        if (parent is null)
        {
            EnsureCanCreateRoot(work, actorUserId);
            EnsureNoSelfAssignment(actorUserId, assigneeUserIds);
            return;
        }

        EnsureCanCreateBranch(parent, actorUserId);
        EnsureNoSelfAssignment(actorUserId, assigneeUserIds);
    }

    public static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_ACTOR_REQUIRED);
    }

    public static void EnsureCanCreateRoot(Work work, string actorUserId)
    {
        EnsureActor(actorUserId);

        if (!string.Equals(work.CreatedByUserId, actorUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_ROOT_CREATE_FORBIDDEN,
                new { workId = work.Id, actorUserId, ownerUserId = work.CreatedByUserId });
    }

    public static void EnsureCanCreateBranch(WorkAssignment parent, string actorUserId)
    {
        EnsureActor(actorUserId);

        var isOwner = string.Equals(parent.CreatedByUserId, actorUserId, StringComparison.Ordinal);
        var isDirectAssignee = parent.Assignees != null &&
                               parent.Assignees.Any(a => string.Equals(a.UserId, actorUserId, StringComparison.Ordinal));

        if (!isOwner && !isDirectAssignee)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_BRANCH_CREATE_FORBIDDEN,
                new { workId = parent.WorkId, parentAssignmentId = parent.Id, actorUserId });
    }

    public static void EnsureNoSelfAssignment(string actorUserId, IEnumerable<string>? assigneeUserIds)
    {
        EnsureActor(actorUserId);

        var hasSelf = (assigneeUserIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Any(x => string.Equals(x, actorUserId, StringComparison.Ordinal));

        if (hasSelf)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_SELF_ASSIGNMENT_NOT_ALLOWED,
                new { actorUserId });
    }
}
