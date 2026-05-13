using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IUnitSelectionService
{
    Task<List<string>> ExpandVirtualUnitIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct);
    Task<List<string>> ResolveUnitManagerUserIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct);
}

// Virtual-unit expansion is assignment-contract logic. General unit pickers keep selected unit ids
// as-is; assignment save/update calls this service before resolving unit-manager accounts.
public sealed class UnitSelectionService : IUnitSelectionService
{
    private readonly MongoDbContext _ctx;

    public UnitSelectionService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<List<string>> ExpandVirtualUnitIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct)
    {
        var inputIds = (unitIds ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (inputIds.Count == 0)
            return new List<string>();

        var selectedUnits = await _ctx.Units
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

            var descendantIds = await _ctx.Units
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

    public async Task<List<string>> ResolveUnitManagerUserIdsAsync(IEnumerable<string>? unitIds, CancellationToken ct)
    {
        var concreteUnitIds = await ExpandVirtualUnitIdsAsync(unitIds, ct);
        if (concreteUnitIds.Count == 0)
            return new List<string>();

        var managers = await _ctx.Users
            .Find(x =>
                x.UnitId != null &&
                concreteUnitIds.Contains(x.UnitId) &&
                x.AccountKind == ManagementAccountKind.UnitManager &&
                !x.IsDeleted)
            .Project(x => new { x.Id, x.UnitId })
            .ToListAsync(ct);

        var foundUnits = managers
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitId))
            .Select(x => x.UnitId!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (foundUnits.Count != concreteUnitIds.Count)
            throw AppExceptionFactory.BadRequest(AppErrorCode.UNIT_MANAGER_MISSING, new { unitIds = concreteUnitIds });

        return managers
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
