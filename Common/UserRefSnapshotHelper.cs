using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Services.Common;

internal static class UserRefSnapshotHelper
{
    internal sealed record UnitLite(
        string Id,
        string? Symbol,
        string? ShortName,
        string? Name
    );

    public static async Task<Dictionary<string, UnitLite>> LoadUnitLiteMapAsync(
        MongoDbContext ctx,
        IEnumerable<string> unitIds,
        CancellationToken ct)
    {
        var list = unitIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (list.Count == 0)
            return new Dictionary<string, UnitLite>(StringComparer.Ordinal);

        var rows = await ctx.Units
            .Find(u => list.Contains(u.Id) && !u.IsDeleted)
            .Project(u => new UnitLite(u.Id, u.Symbol, u.ShortName, u.FullName))
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
    }

    public static UserRef ToUserRef(AppUser u, Dictionary<string, UnitLite> unitMap)
    {
        unitMap.TryGetValue(u.UnitId ?? string.Empty, out var unit);

        return new UserRef
        {
            UserId = u.Id,
            Username = u.Username,
            FullName = u.FullName,
            UnitId = u.UnitId,
            UnitSymbol = unit?.Symbol,
            UnitShortName = unit?.ShortName,
            UnitName = unit?.Name,
            PositionCode = Positions.Normalize(u.PositionCode),
            PositionName = Positions.GetName(u.PositionCode),
        };
    }

    public static async Task<Dictionary<string, UserRef>> LoadUserRefMapAsync(
        MongoDbContext ctx,
        IEnumerable<string> userIds,
        CancellationToken ct)
    {
        var ids = userIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<string, UserRef>(StringComparer.Ordinal);

        var users = await ctx.Users
            .Find(u => ids.Contains(u.Id) && !u.IsDeleted)
            .ToListAsync(ct);

        var unitIds = users
            .Select(u => u.UnitId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unitMap = await LoadUnitLiteMapAsync(ctx, unitIds!, ct);

        var map = new Dictionary<string, UserRef>(StringComparer.Ordinal);
        foreach (var u in users)
            map[u.Id] = ToUserRef(u, unitMap);

        return map;
    }

    public static UserRef NewEmptyUserRef(string userId) => new()
    {
        UserId = userId
    };
}