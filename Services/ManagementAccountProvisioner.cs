using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services;

public static class ManagementAccountKind
{
    public const string UnitManager = "UNIT_MANAGER";
    public const string LevelManager = "LEVEL_MANAGER";
    public const string NormalUser = "NORMAL_USER";
}

public sealed class ManagementAccountConvention
{
    public const string UnitManagerPrefix = "mu_";
    public const string LevelManagerPrefix = "ml_";

    public string BuildUnitManagerUsername(Unit unit)
    {
        // Naming adjustment point: current mu key is Unit.Code because it is generated and unique.
        // If business wants symbol-based names later, change this method only.
        return UnitManagerPrefix + BuildUnitKey(unit);
    }

    public string BuildLevelManagerUsername(int level)
    {
        // Naming adjustment point: current ml key is the numeric Unit.Level.
        return LevelManagerPrefix + BuildLevelKey(level);
    }

    public string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static string BuildUnitKey(Unit unit)
        => (unit.Code ?? unit.Id).Trim().ToLowerInvariant();

    private static string BuildLevelKey(int level)
        => level.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public interface IManagementAccountProvisioner
{
    Task EnsureForUnitAsync(Unit unit, string byUserId, DateTime now, CancellationToken ct);
}

public sealed class ManagementAccountProvisioner : IManagementAccountProvisioner
{
    private readonly MongoDbContext _ctx;
    private readonly PasswordHasher<AppUser> _hasher;
    private readonly ManagementAccountConvention _convention;

    public ManagementAccountProvisioner(
        MongoDbContext ctx,
        PasswordHasher<AppUser> hasher,
        ManagementAccountConvention convention)
    {
        _ctx = ctx;
        _hasher = hasher;
        _convention = convention;
    }

    public async Task EnsureForUnitAsync(Unit unit, string byUserId, DateTime now, CancellationToken ct)
    {
        await EnsureUserAsync(
            username: _convention.BuildUnitManagerUsername(unit),
            fullName: $"Quan tri don vi {unit.ShortName ?? unit.FullName}",
            accountKind: ManagementAccountKind.UnitManager,
            unitId: unit.Id,
            roles: new List<string> { Roles.ManagerUnit(unit.Id) },
            byUserId,
            now,
            ct);

        await EnsureUserAsync(
            username: _convention.BuildLevelManagerUsername(unit.Level),
            fullName: $"Quan tri cap {unit.Level}",
            accountKind: ManagementAccountKind.LevelManager,
            unitId: null,
            roles: new List<string> { Roles.MANAGER_LEVEL },
            byUserId,
            now,
            ct);
    }

    private async Task EnsureUserAsync(
        string username,
        string fullName,
        string accountKind,
        string? unitId,
        List<string> roles,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        var normalized = _convention.NormalizeUsername(username);
        var exists = await _ctx.Users
            .Find(x => x.Username == normalized && !x.IsDeleted)
            .AnyAsync(ct);

        if (exists)
            return;

        var user = new AppUser
        {
            Username = normalized,
            FullName = fullName,
            UnitId = unitId,
            Roles = roles,
            AccountKind = accountKind,
            IsDeleted = false,
            CreatedByUserId = byUserId,
            UpdatedByUserId = byUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Note = "Default management account. Naming is centralized in ManagementAccountConvention."
        };

        user.PasswordHash = _hasher.HashPassword(user, Guid.NewGuid().ToString("N") + "!Aa1");
        await _ctx.Users.InsertOneAsync(user, cancellationToken: ct);
    }
}
