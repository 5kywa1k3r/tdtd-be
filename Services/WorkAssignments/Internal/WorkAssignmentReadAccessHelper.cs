using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentReadAccessHelper
{
    public static async Task<bool> CanReadAssignmentAsync(
        MongoDbContext ctx,
        string? assignmentId,
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignmentId) || string.IsNullOrWhiteSpace(actorUserId))
            return false;

        var hasProjectedRole = await ctx.AssignmentListDocRoles
            .Find(x =>
                x.AssignmentId == assignmentId &&
                x.UserId == actorUserId &&
                !x.IsDeleted &&
                x.Roles.Any())
            .Limit(1)
            .AnyAsync(ct);

        if (hasProjectedRole)
            return true;

        var assignment = await ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return CanReadAssignmentDirectly(assignment, actorUserId);
    }

    public static async Task<bool> CanReadAssignmentOrAncestorAsync(
        MongoDbContext ctx,
        WorkAssignment assignment,
        string actorUserId,
        CancellationToken ct)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(actorUserId))
            return false;

        if (await CanReadAssignmentAsync(ctx, assignment.Id, actorUserId, ct))
            return true;

        var ancestorIds = ResolveAncestorIds(assignment);
        if (ancestorIds.Count == 0)
            return false;

        var hasProjectedAncestorRole = await ctx.AssignmentListDocRoles
            .Find(x =>
                ancestorIds.Contains(x.AssignmentId) &&
                x.UserId == actorUserId &&
                !x.IsDeleted &&
                x.Roles.Any())
            .Limit(1)
            .AnyAsync(ct);

        if (hasProjectedAncestorRole)
            return true;

        var ancestors = await ctx.WorkAssignments
            .Find(x =>
                ancestorIds.Contains(x.Id) &&
                !x.IsDeleted)
            .ToListAsync(ct);

        return ancestors.Any(x => CanReadAssignmentDirectly(x, actorUserId));
    }

    public static async Task<HashSet<string>?> ResolveReadableScopeIdsAsync(
        MongoDbContext ctx,
        string workId,
        string? scopeAssignmentId,
        string actorUserId,
        CancellationToken ct)
    {
        var scopeId = scopeAssignmentId?.Trim();
        if (string.IsNullOrWhiteSpace(scopeId))
            return null;

        var scopeAssignment = await ctx.WorkAssignments
            .Find(x => x.WorkId == workId && x.Id == scopeId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (scopeAssignment is null)
            return new HashSet<string>(StringComparer.Ordinal);

        if (!await CanReadAssignmentOrAncestorAsync(ctx, scopeAssignment, actorUserId, ct))
            return new HashSet<string>(StringComparer.Ordinal);

        var assignments = await ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .Project(x => new { x.Id, x.Path })
            .ToListAsync(ct);

        var scopePath = scopeAssignment.Path?.Trim();
        return assignments
            .Where(x =>
                string.Equals(x.Id, scopeAssignment.Id, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(scopePath) &&
                 !string.IsNullOrWhiteSpace(x.Path) &&
                 x.Path.StartsWith($"{scopePath}/", StringComparison.Ordinal)))
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool CanReadAssignmentDirectly(WorkAssignment? assignment, string actorUserId)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(actorUserId))
            return false;

        return string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal) ||
               (assignment.LeaderWatcherUserIds ?? new List<string>()).Contains(actorUserId) ||
               (assignment.Assignees ?? new List<UserRef>())
                   .Any(x => string.Equals(x.UserId, actorUserId, StringComparison.Ordinal));
    }

    private static List<string> ResolveAncestorIds(WorkAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment.Path))
            return new List<string>();

        return assignment.Path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, assignment.Id, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
