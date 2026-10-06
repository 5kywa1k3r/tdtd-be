using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IUnitSelectionService
{
    Task<List<string>> ExpandVirtualUnitIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct);
    Task<List<string>> ResolveUnitManagerUserIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct);
}

public sealed class UnitSelectionService(MongoDbContext ctx) : IUnitSelectionService
{
    // Legacy expansion is used by aggregate/basic summary; keep its behavior unchanged.
    public async Task<List<string>> ExpandVirtualUnitIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct)
    {
        var inputIds = (unitIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (inputIds.Count == 0)
            return new List<string>();

        var selectedUnits = await ctx.Units
            .Find(x => inputIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        var selectedById = selectedUnits.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var missingIds = inputIds.Where(id => !selectedById.ContainsKey(id)).ToList();
        if (missingIds.Count > 0)
            throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_NOT_FOUND, new { unitIds = missingIds });

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in inputIds.Select(id => selectedById[id]))
        {
            if (!unit.IsVirtual)
            {
                result.Add(unit.Id);
                continue;
            }

            var prefix = (unit.Code ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(prefix))
                continue;

            var descendantIds = await ctx.Units
                .Find(x =>
                    x.Code != null &&
                    x.Code.StartsWith(prefix) &&
                    !x.IsDeleted &&
                    !x.IsVirtual)
                .SortBy(x => x.Code)
                .Project(x => x.Id)
                .ToListAsync(ct);

            foreach (var id in descendantIds)
                result.Add(id);
        }

        return result.ToList();
    }

    private async Task<List<string>> ExpandAssignmentUnitIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct)
    {
        var ids = (unitIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        if (ids.Count == 0) return [];
        if (ids.Count > 2000 || ids.Any(x => !ObjectId.TryParse(x, out _)))
            throw Invalid("ID đơn vị nhận việc không hợp lệ.");
        var selected = await ctx.Units.Find(x => ids.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct);
        if (selected.Count != ids.Count || selected.Any(x => !UnitManagementScope.Contains(x.Code, x.Code)))
            throw Invalid("Đơn vị nhận việc không còn hợp lệ hoặc mã đơn vị sai cấu trúc.");
        var groups = selected.Where(x => x.IsVirtual).ToList();
        var descendants = new List<Unit>();
        if (groups.Count > 0)
        {
            var f = Builders<Unit>.Filter;
            descendants = await ctx.Units.Find(f.Eq(x => x.IsDeleted, false) & f.Eq(x => x.IsVirtual, false)
                & f.Or(groups.Select(g => f.Regex(x => x.Code, new BsonRegularExpression("^" + g.Code)))))
                .ToListAsync(ct);
        }
        if (!WorkAssignmentUnitRecipients.TryExpand(selected, descendants, out var concrete, out var error))
            throw Invalid(error!);
        return concrete.Select(x => x.Id).ToList();
    }

    public async Task<List<string>> ResolveUnitManagerUserIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct)
    {
        var ids = await ExpandAssignmentUnitIdsAsync(unitIds, ct);
        if (ids.Count == 0) return [];
        var managers = await ctx.Users.Find(x => x.UnitId != null && ids.Contains(x.UnitId)
            && x.AccountKind == ManagementAccountKind.UnitManager && !x.IsDeleted)
            .Project(x => new AppUser { Id = x.Id, UnitId = x.UnitId, AccountKind = x.AccountKind }).ToListAsync(ct);
        return WorkAssignmentUnitRecipients.RequireSingleManagers(ids, managers);
    }

    private static AppException Invalid(string message) => AppExceptionFactory.BadRequest(
        AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID, message: message);
}
