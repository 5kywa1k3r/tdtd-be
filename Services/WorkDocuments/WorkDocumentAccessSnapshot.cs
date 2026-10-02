using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkDocuments;

/// <summary>One live-tree policy for listing, download and mutations. Stored file paths are display metadata only.</summary>
public sealed class WorkDocumentAccessSnapshot
{
    private readonly Dictionary<string, WorkAssignment> nodes;
    private readonly Dictionary<string, HashSet<string>> paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> memberIds;
    public string WorkId { get; }
    public string UserId { get; }
    public bool CanReadWorkDocuments { get; }
    public bool IsWorkOwner { get; }
    public IReadOnlyList<WorkAssignment> ReadableAssignments { get; }
    public IReadOnlyList<WorkAssignment> UploadAssignments { get; }

    public WorkDocumentAccessSnapshot(string workId, string userId, bool canReadWork, bool isWorkOwner,
        IEnumerable<WorkAssignment> assignments, IEnumerable<string> grantedAssignmentIds)
    {
        WorkId = workId; UserId = userId; CanReadWorkDocuments = canReadWork; IsWorkOwner = isWorkOwner;
        nodes = assignments.Where(a => a.WorkId == workId && a.IsActive && !a.IsDeleted).ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            WorkAssignment? cursor = node;
            while (cursor != null)
            {
                if (!path.Add(cursor.Id)) { path.Clear(); break; }
                if (string.IsNullOrWhiteSpace(cursor.ParentAssignmentId)) break;
                if (!nodes.TryGetValue(cursor.ParentAssignmentId, out cursor)) { path.Clear(); break; }
            }
            if (path.Count > 0) paths[node.Id] = path;
        }
        memberIds = nodes.Values.Where(a => paths.ContainsKey(a.Id) &&
            (WorkAssignmentCurrentAuthority.IsReviewer(a, userId) || (a.Assignees?.Any(u => u.UserId == userId) == true) ||
             (a.LeaderWatcherUserIds?.Contains(userId) == true) || (a.LeaderWatchers?.Any(u => u.UserId == userId) == true)))
            .Select(a => a.Id).Concat(grantedAssignmentIds.Where(paths.ContainsKey)).ToHashSet(StringComparer.Ordinal);
        ReadableAssignments = nodes.Values.Where(a => CanReadAssignment(a.Id)).OrderBy(a => AssignmentPath(a.Id)).ToArray();
        UploadAssignments = nodes.Values.Where(a => paths.ContainsKey(a.Id) && WorkAssignmentCurrentAuthority.IsReviewer(a, userId))
            .OrderBy(a => a.Path).ToArray();
    }

    public bool CanReadAssignment(string id) => paths.ContainsKey(id) &&
        (IsWorkOwner || memberIds.Any(member => paths[id].Contains(member) || paths[member].Contains(id)));

    public string AssignmentPath(string id)
    {
        if (!paths.ContainsKey(id)) return "";
        var ids = new List<string>();
        var cursor = nodes[id];
        while (true) { ids.Add(cursor.Id); if (string.IsNullOrWhiteSpace(cursor.ParentAssignmentId)) break; cursor = nodes[cursor.ParentAssignmentId]; }
        ids.Reverse();
        return "/" + string.Join("/", ids);
    }

    public bool IsWithinBranch(string id, string selectedId) => paths.TryGetValue(id, out var path) && path.Contains(selectedId);

    public bool CanRead(FileDoc file, WorkDocumentScopeInfo scope)
    {
        if (file.IsDeleted || file.SourceType == "REPORT_EVIDENCE") return false;
        if (scope.Scope == WorkDocumentConstants.ScopeWork) return scope.WorkId == WorkId && CanReadWorkDocuments;
        if (scope.Scope == WorkDocumentConstants.ScopeAssignmentBranch)
            return scope.WorkId == WorkId && scope.AssignmentId != null && CanReadAssignment(scope.AssignmentId);
        return file.CreatedByUserId == UserId;
    }

    public bool CanDelete(FileDoc file, WorkDocumentScopeInfo scope)
    {
        if (file.IsDeleted || file.SourceType == "REPORT_EVIDENCE") return false;
        if (scope.Scope == WorkDocumentConstants.ScopeWork) return scope.WorkId == WorkId && IsWorkOwner;
        if (scope.Scope == WorkDocumentConstants.ScopeAssignmentBranch)
            return scope.WorkId == WorkId && scope.AssignmentId != null && paths.ContainsKey(scope.AssignmentId) &&
                (IsWorkOwner || WorkAssignmentCurrentAuthority.IsReviewer(nodes[scope.AssignmentId], UserId));
        return file.CreatedByUserId == UserId;
    }
}
