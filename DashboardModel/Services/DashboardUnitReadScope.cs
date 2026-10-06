using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.DashboardModel.Services;

// Dashboard read authority only. Does not grant Work mutation or report payload access.
internal sealed class DashboardUnitReadScope
{
    internal HashSet<string> FullWorkIds { get; }
    internal List<WorkAssignment> Assignments { get; }
    internal HashSet<string> WorkIds { get; }
    internal List<WorkAssignment> CreatedAssignments { get; private set; } = new();
    internal List<WorkAssignment> ReceivedAssignments { get; private set; } = new();
    private readonly Dictionary<string, HashSet<string>> _restrictedRecipients;
    internal string CacheScope { get; }
    internal string Label { get; private set; } = string.Empty;

    private DashboardUnitReadScope(string unitCode, HashSet<string> fullWorkIds,
        List<WorkAssignment> assignments, Dictionary<string, HashSet<string>> restrictedRecipients)
    {
        FullWorkIds = fullWorkIds;
        Assignments = assignments;
        _restrictedRecipients = restrictedRecipients;
        WorkIds = fullWorkIds.Concat(assignments.Select(x => x.WorkId)).ToHashSet(StringComparer.Ordinal);
        // Resolve current authority BEFORE reading Redis; revoked branches/recipients cannot reuse old entries.
        var authority = unitCode + "|" + string.Join(",", fullWorkIds.Order()) + "|"
            + string.Join(";", assignments.OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Path + ":"
                + (restrictedRecipients.TryGetValue(x.Id, out var users) ? string.Join(",", users.Order()) : "*")));
        CacheScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
    }

    internal static bool IsUnitHead(string? position, IEnumerable<string> unitTypes)
    {
        var types = unitTypes.Select(x => x?.Trim().ToUpperInvariant() ?? "").ToHashSet();
        return position?.Trim().ToUpperInvariant() switch
        {
            "TRUONG_PHONG" => types.Contains("PHONG"),
            "TRUONG_CONG_AN_XA" or "TRUONG_CONG_AN_PHUONG" => types.Contains("PHUONG_XA"),
            _ => false
        };
    }

    internal static async Task<DashboardUnitReadScope?> ResolveAsync(MongoDbContext ctx, MeResponse me, CancellationToken ct)
    {
        if (me.IsDeleted || !IsUnitHead(me.PositionCode, me.UnitTypeCodes)) return null;
        var unit = await ctx.Units.Find(x => x.Id == me.UnitId && !x.IsDeleted && !x.IsVirtual).FirstOrDefaultAsync(ct);
        if (unit is null || !IsUnitHead(me.PositionCode, unit.UnitTypeCodes)
            || !UnitManagementScope.Contains(unit.Code, unit.Code)) return null;

        var units = await ctx.Units.Find(Builders<Unit>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<Unit>.Filter.Eq(x => x.IsVirtual, false)
            & Builders<Unit>.Filter.Regex(x => x.Code, new BsonRegularExpression("^" + Regex.Escape(unit.Code!))))
            .ToListAsync(ct);
        var unitIds = units.Where(x => UnitManagementScope.Contains(unit.Code, x.Code)).Select(x => x.Id).ToList();
        var users = await ctx.Users.Find(x => !x.IsDeleted && unitIds.Contains(x.UnitId!))
            .Project(x => x.Id).ToListAsync(ct);
        var fullRoleWorkIds = await ctx.DocRoles.Find(x => !x.IsDeleted && x.UserId == me.Id && x.DocType == DocType.WORK
            && (x.Role == DocRoleType.OWNER || x.Role == DocRoleType.LEADER_DIRECTIVE || x.Role == DocRoleType.LEADER_WATCH))
            .Project(x => x.DocId).ToListAsync(ct);
        var fullWorkIds = (await ctx.Works.Find(x => !x.IsDeleted && (x.CreatedByUserId == me.Id
            || fullRoleWorkIds.Contains(x.Id)
            || (x.Owner != null && unitIds.Contains(x.Owner.UnitId!))
            || ((x.Owner == null || x.Owner.UnitId == null) && users.Contains(x.CreatedByUserId!))))
            .Project(x => x.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var roleAssignmentIds = await ctx.DocRoles.Find(x => !x.IsDeleted && x.UserId == me.Id && x.DocType == DocType.WORK_ASSIGNMENT
            && (x.Role == DocRoleType.ASSIGNEE || x.Role == DocRoleType.ASSIGNER || x.Role == DocRoleType.ASSIGNMENT_LEADER_WATCH))
            .Project(x => x.DocId).ToListAsync(ct);

        var seeds = await ctx.WorkAssignments.Find(x => !x.IsDeleted && x.IsActive && (
            fullWorkIds.Contains(x.WorkId) || x.CreatedByUserId == me.Id || (x.IssuedByUnitId == null && users.Contains(x.CreatedByUserId!))
            || unitIds.Contains(x.IssuedByUnitId!) || roleAssignmentIds.Contains(x.Id)
            || x.Assignees.Any(a => a.UserId == me.Id || unitIds.Contains(a.UnitId!))))
            .ToListAsync(ct);
        var fullSeeds = seeds.Where(x => fullWorkIds.Contains(x.WorkId) || x.CreatedByUserId == me.Id
            || (x.IssuedByUnitId == null && users.Contains(x.CreatedByUserId!)) || unitIds.Contains(x.IssuedByUnitId!)
            || roleAssignmentIds.Contains(x.Id) || x.Assignees.Any(a => a.UserId == me.Id)).ToList();
        var fb = Builders<WorkAssignment>.Filter;
        var branchSeeds = new List<WorkAssignment>();
        foreach (var seed in fullSeeds.Where(x => !fullWorkIds.Contains(x.WorkId) && !string.IsNullOrWhiteSpace(x.Path))
                     .OrderBy(x => x.Path.Length))
            if (!branchSeeds.Any(parent => InBranch(seed, parent))) branchSeeds.Add(seed);
        var branches = branchSeeds.Select(x =>
            fb.Eq(a => a.WorkId, x.WorkId) & fb.Regex(a => a.Path, new BsonRegularExpression("^" + Regex.Escape(x.Path) + "(?:/|$)"))).ToList();
        branches.Add(fb.In(x => x.Id, seeds.Select(x => x.Id)));
        branches.Add(fb.In(x => x.WorkId, fullWorkIds));
        var assignments = await ctx.WorkAssignments.Find(fb.Eq(x => x.IsDeleted, false) & fb.Eq(x => x.IsActive, true) & fb.Or(branches))
            .ToListAsync(ct);
        var candidateWorkIds = assignments.Select(x => x.WorkId).Distinct().ToList();
        var activeWorkIds = (await ctx.Works.Find(x => !x.IsDeleted && candidateWorkIds.Contains(x.Id))
            .Project(x => x.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        assignments = assignments.Where(x => activeWorkIds.Contains(x.WorkId)).ToList();
        var result = Create(unit.Code!, unitIds.ToHashSet(), fullWorkIds, fullSeeds, assignments);
        result.Label = unit.FullName + (unitIds.Count > 1 ? " và đơn vị trực thuộc" : "");
        var created = seeds.Where(x => x.CreatedByUserId == me.Id || (x.IssuedByUnitId == null && users.Contains(x.CreatedByUserId!)) || unitIds.Contains(x.IssuedByUnitId!)).ToList();
        var received = seeds.Where(x => x.Assignees.Any(a => a.UserId == me.Id || unitIds.Contains(a.UnitId!))).ToList();
        result.CreatedAssignments = result.Assignments.Where(x => created.Any(seed => InBranch(x, seed))).ToList();
        result.ReceivedAssignments = result.Assignments.Where(x => received.Any(seed => InBranch(x, seed))).ToList();
        return result;
    }

    internal static DashboardUnitReadScope Create(string unitCode, HashSet<string> unitIds, HashSet<string> fullWorkIds,
        List<WorkAssignment> fullSeeds, List<WorkAssignment> candidates)
    {
        var restrictions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var visible = new List<WorkAssignment>();
        foreach (var assignment in candidates)
        {
            var full = fullWorkIds.Contains(assignment.WorkId) || fullSeeds.Any(seed => InBranch(assignment, seed));
            if (!full)
            {
                var recipients = assignment.Assignees.Where(a => a.UnitId != null && unitIds.Contains(a.UnitId)).ToList();
                if (recipients.Count == 0) continue;
                restrictions[assignment.Id] = recipients.Select(x => x.UserId!).ToHashSet(StringComparer.Ordinal);
                assignment.Assignees = recipients;
            }
            visible.Add(assignment);
        }
        // Keep the expansion affordance and counters accurate for the visible children only.
        var childrenByParent = visible.Where(x => !string.IsNullOrWhiteSpace(x.ParentAssignmentId)).ToLookup(x => x.ParentAssignmentId!);
        foreach (var assignment in visible.Where(x => restrictions.ContainsKey(x.Id)))
        {
            var children = childrenByParent[assignment.Id].ToList();
            assignment.ActiveChildCount = children.Count;
            assignment.ChildProgressCounts = new();
            foreach (var child in children) assignment.ChildProgressCounts.Add((WorkAssignmentProgressStatus)child.ProgressStatus);
            assignment.WorstChildProgressStatus = children.Count == 0 ? null : (int)assignment.ChildProgressCounts.GetWorstStatus();
            assignment.WorstEvaluationCode = null;
            assignment.WorstEvaluationLabel = null;
            assignment.EvaluatedAssignmentCount = children.Count(x => !string.IsNullOrWhiteSpace(x.EvaluationCode));
        }
        return new(unitCode, fullWorkIds, visible, restrictions);
    }

    internal static bool InBranch(WorkAssignment node, WorkAssignment root) => node.WorkId == root.WorkId
        && (node.Id == root.Id || (!string.IsNullOrWhiteSpace(root.Path) && !string.IsNullOrWhiteSpace(node.Path)
            && (node.Path == root.Path || node.Path.StartsWith(root.Path + "/", StringComparison.Ordinal))));

    internal List<WorkAssignment> Entries(string workId)
    {
        var nodes = Assignments.Where(x => x.WorkId == workId).ToList();
        var paths = nodes.Where(x => !string.IsNullOrWhiteSpace(x.Path)).Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        return nodes.Where(node => !HasVisibleAncestor(node.Path, paths)).ToList();
    }

    private static bool HasVisibleAncestor(string? path, HashSet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var index = path.LastIndexOf('/');
        while (index > 0)
        {
            if (paths.Contains(path[..index])) return true;
            index = path.LastIndexOf('/', index - 1);
        }
        return false;
    }
    internal bool AllowsRecipient(string assignmentId, string? userId) => Assignments.Any(x => x.Id == assignmentId)
        && (!_restrictedRecipients.TryGetValue(assignmentId, out var users) || (userId != null && users.Contains(userId)));
    internal WorkAssignment Mask(WorkAssignment node) => Assignments.FirstOrDefault(x => x.Id == node.Id) ?? node;
    internal FilterDefinition<WorkReportPeriod> PeriodFilter()
    {
        var fb = Builders<WorkReportPeriod>.Filter;
        var filters = new List<FilterDefinition<WorkReportPeriod>> { fb.In(x => x.WorkAssignmentId,
            Assignments.Where(x => !_restrictedRecipients.ContainsKey(x.Id)).Select(x => x.Id)) };
        filters.AddRange(_restrictedRecipients.Select(pair => fb.Eq(x => x.WorkAssignmentId, pair.Key) & fb.In(x => x.AssigneeUserId, pair.Value)));
        return fb.Or(filters);
    }
}
