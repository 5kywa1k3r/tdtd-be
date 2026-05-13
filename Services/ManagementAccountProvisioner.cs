using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services;

public static class ManagementAccountKind
{
    public const string SystemAdmin = "SYSTEM_ADMIN";
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
        return UnitManagerPrefix + BuildSymbolKey(unit);
    }

    public string BuildLevelManagerUsername(Unit unit)
    {
        return LevelManagerPrefix + BuildSymbolKey(unit);
    }

    public string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    public string BuildLegacyUnitManagerUsername(Unit unit)
        => UnitManagerPrefix + BuildLegacyUnitKey(unit);

    public string BuildLegacyLevelManagerUsername(Unit unit)
        => LevelManagerPrefix + unit.Level.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsLegacyLevelManagerUsername(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        if (!value.StartsWith(LevelManagerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = value[LevelManagerPrefix.Length..].Trim();
        return int.TryParse(raw, out _);
    }

    private static string BuildSymbolKey(Unit unit)
        => FirstNonBlank(unit.Symbol, unit.Code, unit.Id).Trim().ToLowerInvariant();

    private static string BuildLegacyUnitKey(Unit unit)
        => (unit.Code ?? unit.Id).Trim().ToLowerInvariant();

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}

public sealed class ManagementAccountProvisionResult
{
    public int CreatedCount { get; private set; }
    public int UpdatedCount { get; private set; }
    public int DisabledCount { get; private set; }
    public List<string> SessionInvalidationUserIds { get; } = new();

    internal void Add(ManagementAccountUserEnsureResult result)
    {
        CreatedCount += result.Created ? 1 : 0;
        UpdatedCount += result.Updated ? 1 : 0;
        DisabledCount += result.DisabledCount;

        foreach (var id in result.SessionInvalidationUserIds)
        {
            if (!SessionInvalidationUserIds.Contains(id, StringComparer.Ordinal))
                SessionInvalidationUserIds.Add(id);
        }
    }
}

internal sealed class ManagementAccountUserEnsureResult
{
    public bool Created { get; init; }
    public bool Updated { get; init; }
    public int DisabledCount { get; init; }
    public List<string> SessionInvalidationUserIds { get; init; } = new();
}

public interface IManagementAccountProvisioner
{
    Task<ManagementAccountProvisionResult> EnsureForUnitAsync(Unit unit, string byUserId, DateTime now, CancellationToken ct);
}

public sealed class ManagementAccountProvisioner : IManagementAccountProvisioner
{
    private const string DefaultGeneratedAccountPassword = "123456@Aa";

    private readonly MongoDbContext _ctx;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly ManagementAccountConvention _convention;

    public ManagementAccountProvisioner(
        MongoDbContext ctx,
        IPasswordHasher<AppUser> hasher,
        ManagementAccountConvention convention)
    {
        _ctx = ctx;
        _hasher = hasher;
        _convention = convention;
    }

    public async Task<ManagementAccountProvisionResult> EnsureForUnitAsync(Unit unit, string byUserId, DateTime now, CancellationToken ct)
    {
        var result = new ManagementAccountProvisionResult();

        result.Add(await EnsureUserAsync(
            username: _convention.BuildUnitManagerUsername(unit),
            fullName: $"Quản trị đơn vị {unit.ShortName ?? unit.FullName}",
            accountKind: ManagementAccountKind.UnitManager,
            unitId: unit.Id,
            roles: new List<string> { Roles.ManagerUnit(unit.Id) },
            legacyUsernames: new[] { _convention.BuildLegacyUnitManagerUsername(unit) },
            byUserId,
            now,
            ct));

        result.Add(await EnsureUserAsync(
            username: _convention.BuildLevelManagerUsername(unit),
            fullName: $"Quản trị cấp {unit.Level} - {unit.ShortName ?? unit.FullName}",
            accountKind: ManagementAccountKind.LevelManager,
            unitId: unit.Id,
            roles: new List<string> { Roles.MANAGER_LEVEL },
            legacyUsernames: Array.Empty<string>(),
            byUserId,
            now,
            ct));

        return result;
    }

    private async Task<ManagementAccountUserEnsureResult> EnsureUserAsync(
        string username,
        string fullName,
        string accountKind,
        string? unitId,
        List<string> roles,
        IEnumerable<string> legacyUsernames,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        var normalized = _convention.NormalizeUsername(username);
        var legacyNames = legacyUsernames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(_convention.NormalizeUsername)
            .Where(x => !string.Equals(x, normalized, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingByTargetUsername = await _ctx.Users
            .Find(x => x.Username == normalized && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (existingByTargetUsername is not null &&
            (!string.Equals(existingByTargetUsername.AccountKind, accountKind, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existingByTargetUsername.UnitId ?? string.Empty, unitId ?? string.Empty, StringComparison.Ordinal)))
        {
            throw AppExceptionFactory.Create(AppErrorCode.USER_ADMIN_USERNAME_DUPLICATE, new
            {
                username = normalized,
                expectedAccountKind = accountKind,
                expectedUnitId = unitId,
                actualUserId = existingByTargetUsername.Id,
                actualAccountKind = existingByTargetUsername.AccountKind,
                actualUnitId = existingByTargetUsername.UnitId
            });
        }

        var reusable = existingByTargetUsername
            ?? await FindReusableManagementUserAsync(accountKind, unitId, legacyNames, ct);

        if (reusable is not null)
            return await UpdateExistingUserAsync(reusable, normalized, fullName, accountKind, unitId, roles, byUserId, now, ct);

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

        user.PasswordHash = _hasher.HashPassword(user, DefaultGeneratedAccountPassword);
        await _ctx.Users.InsertOneAsync(user, cancellationToken: ct);

        return new ManagementAccountUserEnsureResult { Created = true };
    }

    private async Task<AppUser?> FindReusableManagementUserAsync(
        string accountKind,
        string? unitId,
        List<string> legacyNames,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(unitId))
        {
            var unitUser = await _ctx.Users
                .Find(x =>
                    x.UnitId == unitId &&
                    x.AccountKind == accountKind &&
                    !x.IsDeleted)
                .SortBy(x => x.Username)
                .FirstOrDefaultAsync(ct);

            if (unitUser is not null)
                return unitUser;
        }

        if (legacyNames.Count == 0)
            return null;

        return await _ctx.Users
            .Find(x =>
                legacyNames.Contains(x.Username) &&
                x.AccountKind == accountKind &&
                !x.IsDeleted)
            .SortBy(x => x.Username)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<ManagementAccountUserEnsureResult> UpdateExistingUserAsync(
        AppUser existing,
        string username,
        string fullName,
        string accountKind,
        string? unitId,
        List<string> roles,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        var needsSessionInvalidation =
            !string.Equals(existing.Username, username, StringComparison.Ordinal) ||
            !string.Equals(existing.UnitId ?? string.Empty, unitId ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(existing.AccountKind, accountKind, StringComparison.OrdinalIgnoreCase) ||
            !RolesEqual(existing.Roles, roles);

        var update = Builders<AppUser>.Update
            .Set(x => x.Username, username)
            .Set(x => x.FullName, fullName)
            .Set(x => x.AccountKind, accountKind)
            .Set(x => x.UnitId, unitId)
            .Set(x => x.Roles, roles)
            .Set(x => x.IsDeleted, false)
            .Set(x => x.UpdatedByUserId, byUserId)
            .Set(x => x.UpdatedAtUtc, now);

        var updateResult = await _ctx.Users.UpdateOneAsync(x => x.Id == existing.Id, update, cancellationToken: ct);
        var disabledUserIds = await DisableDuplicateManagementAccountsAsync(accountKind, unitId, existing.Id, byUserId, now, ct);

        var invalidationIds = new List<string>();
        if (needsSessionInvalidation)
            invalidationIds.Add(existing.Id);
        invalidationIds.AddRange(disabledUserIds);

        return new ManagementAccountUserEnsureResult
        {
            Updated = updateResult.ModifiedCount > 0,
            DisabledCount = disabledUserIds.Count,
            SessionInvalidationUserIds = invalidationIds
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    private async Task<List<string>> DisableDuplicateManagementAccountsAsync(
        string accountKind,
        string? unitId,
        string keepUserId,
        string byUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(unitId))
            return new List<string>();

        var duplicates = await _ctx.Users
            .Find(x =>
                x.Id != keepUserId &&
                x.UnitId == unitId &&
                x.AccountKind == accountKind &&
                !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);

        if (duplicates.Count == 0)
            return duplicates;

        var update = Builders<AppUser>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, byUserId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, byUserId);

        await _ctx.Users.UpdateManyAsync(x => duplicates.Contains(x.Id), update, cancellationToken: ct);
        return duplicates;
    }

    private static bool RolesEqual(List<string>? left, List<string>? right)
    {
        var l = left ?? new List<string>();
        var r = right ?? new List<string>();
        return l.Count == r.Count && !l.Except(r, StringComparer.OrdinalIgnoreCase).Any();
    }
}
