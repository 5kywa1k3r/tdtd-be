using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;

namespace tdtd_be.DashboardModel.Services;

internal static class DashboardAuthorityReader
{
    internal static async Task<MeResponse> ReadAsync(MongoDbContext ctx, string actorId, CancellationToken ct)
    {
        var actor = await ctx.Users.Find(x => x.Id == actorId && !x.IsDeleted).Project(x => new { x.Id, x.Username, x.FullName, x.UnitId, x.Roles, x.PositionCode, x.AccountKind }).FirstOrDefaultAsync(ct);
        if (actor is null) throw AppExceptionFactory.Forbidden(AppErrorCode.DASHBOARD_WORK_READ_FORBIDDEN, new { actorId });
        var unit = string.IsNullOrWhiteSpace(actor.UnitId) ? null : await ctx.Units.Find(x => x.Id == actor.UnitId && !x.IsDeleted && !x.IsVirtual).FirstOrDefaultAsync(ct);
        var types = unit is null ? new List<string>() : await ctx.UnitTypes.Find(x => !x.IsDeleted && unit.UnitTypeCodes.Contains(x.Code)).Project(x => x.Code).ToListAsync(ct);
        return new(actor.Id, actor.Username, actor.FullName, types, unit?.Id ?? "", unit?.Symbol, unit?.ShortName, unit?.Code, actor.Roles, actor.PositionCode, false, actor.AccountKind);
    }
}
