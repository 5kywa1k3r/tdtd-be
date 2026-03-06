using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentUserHelper
{
    public static async Task<List<UserRef>> BuildAssigneesAsync(
        MongoDbContext ctx,
        List<string> assigneeUserIds,
        CancellationToken ct)
    {
        var userIds = (assigneeUserIds ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (userIds.Count == 0)
            return new List<UserRef>();

        var users = await ctx.Users
            .Find(x => userIds.Contains(x.Id!) && !x.IsDeleted)
            .ToListAsync(ct);

        if (users.Count != userIds.Count)
            throw new InvalidOperationException("Có người dùng không tồn tại hoặc đã bị xóa.");

        var unitIds = users
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitId))
            .Select(x => x.UnitId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var units = await ctx.Units
            .Find(x => unitIds.Contains(x.Id!) && !x.IsDeleted)
            .ToListAsync(ct);

        var unitMap = units.ToDictionary(x => x.Id!, x => x, StringComparer.Ordinal);

        return users
            .OrderBy(x => userIds.IndexOf(x.Id!))
            .Select(user =>
            {
                unitMap.TryGetValue(user.UnitId ?? string.Empty, out var unit);

                return new UserRef
                {
                    UserId = user.Id!,
                    Username = user.Username ?? string.Empty,
                    FullName = user.FullName ?? string.Empty,
                    UnitId = user.UnitId,
                    UnitSymbol = unit?.Symbol,
                    UnitShortName = unit?.ShortName,
                    UnitName = unit?.FullName,
                    PositionCode = user.PositionCode,
                    PositionName = MapPositionName(user.PositionCode)
                };
            })
            .ToList();
    }

    public static async Task<List<UserRef>> BuildLeaderWatchersAsync(
        MongoDbContext ctx,
        List<string> leaderWatcherUserIds,
        List<UserRef> assignees,
        CancellationToken ct)
    {
        var watcherIds = (leaderWatcherUserIds ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (watcherIds.Count == 0)
            return new List<UserRef>();

        var allowedUnitIds = assignees
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitId))
            .Select(x => x.UnitId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var users = await ctx.Users
            .Find(x => watcherIds.Contains(x.Id!) && !x.IsDeleted)
            .ToListAsync(ct);

        if (users.Count != watcherIds.Count)
            throw new InvalidOperationException("Có leader watcher không tồn tại hoặc đã bị xóa.");

        if (users.Any(x =>
                string.IsNullOrWhiteSpace(x.UnitId) ||
                !allowedUnitIds.Contains(x.UnitId!, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Leader watcher phải thuộc cùng đơn vị với assignee của nhánh.");
        }

        var unitIds = users
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitId))
            .Select(x => x.UnitId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var units = await ctx.Units
            .Find(x => unitIds.Contains(x.Id!) && !x.IsDeleted)
            .ToListAsync(ct);

        var unitMap = units.ToDictionary(x => x.Id!, x => x, StringComparer.Ordinal);

        return users
            .OrderBy(x => watcherIds.IndexOf(x.Id!))
            .Select(user =>
            {
                unitMap.TryGetValue(user.UnitId ?? string.Empty, out var unit);

                return new UserRef
                {
                    UserId = user.Id!,
                    Username = user.Username ?? string.Empty,
                    FullName = user.FullName ?? string.Empty,
                    UnitId = user.UnitId,
                    UnitSymbol = unit?.Symbol,
                    UnitShortName = unit?.ShortName,
                    UnitName = unit?.FullName,
                    PositionCode = user.PositionCode,
                    PositionName = MapPositionName(user.PositionCode)
                };
            })
            .ToList();
    }

    private static string? MapPositionName(string? positionCode)
    {
        if (string.IsNullOrWhiteSpace(positionCode))
            return null;

        return positionCode;
    }
}