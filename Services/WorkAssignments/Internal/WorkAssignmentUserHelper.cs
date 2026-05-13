using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentUserHelper
{
    public static Task<List<UserRef>> BuildAssigneesAsync(
        MongoDbContext ctx,
        List<string> assigneeUserIds,
        CancellationToken ct,
        IEnumerable<UserRef>? seedUsers = null)
    {
        return BuildUsersAsync(
            ctx: ctx,
            userIdsInput: assigneeUserIds,
            ct: ct,
            notFoundCode: AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_USER_NOT_FOUND,
            allowedUnitIds: null,
            invalidUnitCode: null,
            seedUsers: seedUsers);
    }

    public static Task<List<UserRef>> BuildLeaderWatchersAsync(
        MongoDbContext ctx,
        List<string> leaderWatcherUserIds,
        List<UserRef> assignees,
        CancellationToken ct,
        IEnumerable<UserRef>? seedUsers = null)
    {
        var allowedUnitIds = (assignees ?? new List<UserRef>())
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitId))
            .Select(x => x.UnitId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return BuildUsersAsync(
            ctx: ctx,
            userIdsInput: leaderWatcherUserIds,
            ct: ct,
            notFoundCode: AppErrorCode.WORK_ASSIGNMENT_LEADER_WATCHER_NOT_FOUND,
            allowedUnitIds: allowedUnitIds,
            invalidUnitCode: AppErrorCode.WORK_ASSIGNMENT_LEADER_WATCHER_UNIT_MISMATCH,
            seedUsers: seedUsers);
    }

    private static async Task<List<UserRef>> BuildUsersAsync(
        MongoDbContext ctx,
        List<string>? userIdsInput,
        CancellationToken ct,
        AppErrorCode notFoundCode,
        List<string>? allowedUnitIds,
        AppErrorCode? invalidUnitCode,
        IEnumerable<UserRef>? seedUsers = null)
    {
        var userIds = (userIdsInput ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (userIds.Count == 0)
            return new List<UserRef>();

        var seedMap = (seedUsers ?? Array.Empty<UserRef>())
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.UserId))
            .GroupBy(x => x.UserId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var missingIds = userIds
            .Where(id => !seedMap.ContainsKey(id))
            .ToList();

        var dbRefs = new List<UserRef>();
        if (missingIds.Count > 0)
        {
            var users = await ctx.Users
                .Find(x => missingIds.Contains(x.Id!) && !x.IsDeleted)
                .ToListAsync(ct);

            if (users.Count != missingIds.Count)
                throw AppExceptionFactory.BadRequest(notFoundCode, new { userIds });

            var unitIds = users
                .Where(x => !string.IsNullOrWhiteSpace(x.UnitId))
                .Select(x => x.UnitId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var units = unitIds.Count == 0
                ? new List<Unit>()
                : await ctx.Units
                    .Find(x => unitIds.Contains(x.Id!) && !x.IsDeleted)
                    .ToListAsync(ct);

            if (units.Count != unitIds.Count)
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.WORK_ASSIGNMENT_USER_UNIT_INACTIVE,
                    new { userIds, unitIds });

            var unitMap = units.ToDictionary(x => x.Id!, x => x, StringComparer.Ordinal);

            dbRefs = users
                .Select(user =>
                {
                    unitMap.TryGetValue(user.UnitId ?? string.Empty, out var unit);
                    return ToUserRef(user, unit);
                })
                .ToList();
        }

        var finalMap = new Dictionary<string, UserRef>(StringComparer.Ordinal);

        foreach (var kv in seedMap)
            finalMap[kv.Key] = NormalizeUserRef(kv.Value);

        foreach (var item in dbRefs)
            finalMap[item.UserId!] = item;

        if (finalMap.Count != userIds.Count || userIds.Any(id => !finalMap.ContainsKey(id)))
            throw AppExceptionFactory.BadRequest(notFoundCode, new { userIds });

        var result = userIds
            .Select(id => finalMap[id])
            .ToList();

        if (allowedUnitIds is not null && allowedUnitIds.Count > 0)
        {
            if (result.Any(x =>
                    string.IsNullOrWhiteSpace(x.UnitId) ||
                    !allowedUnitIds.Contains(x.UnitId!, StringComparer.Ordinal)))
            {
                throw AppExceptionFactory.BadRequest(
                    invalidUnitCode ?? AppErrorCode.WORK_ASSIGNMENT_USER_UNIT_INVALID,
                    new { userIds, allowedUnitIds });
            }
        }

        return result;
    }

    private static UserRef ToUserRef(AppUser user, Unit? unit)
    {
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
            PositionName = Positions.GetName(user.PositionCode)
        };
    }

    private static UserRef NormalizeUserRef(UserRef input)
    {
        return new UserRef
        {
            UserId = input.UserId,
            Username = input.Username ?? string.Empty,
            FullName = input.FullName ?? string.Empty,
            UnitId = input.UnitId,
            UnitSymbol = input.UnitSymbol,
            UnitShortName = input.UnitShortName,
            UnitName = input.UnitName,
            PositionCode = input.PositionCode,
            PositionName = !string.IsNullOrWhiteSpace(input.PositionName)
                ? input.PositionName
                : Positions.GetName(input.PositionCode)
        };
    }
}
