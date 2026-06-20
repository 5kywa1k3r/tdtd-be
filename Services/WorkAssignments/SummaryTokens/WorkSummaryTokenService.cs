using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;
using tdtd_be.Models;
using tdtd_be.Options;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public sealed class WorkSummaryTokenService : IWorkSummaryTokenService
{
    private const int MaxGrantUnits = 1000;
    private static readonly Regex MonthKeyRegex = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    private readonly MongoDbContext _ctx;
    private readonly SummaryTokenOptions _options;

    public WorkSummaryTokenService(
        MongoDbContext ctx,
        IOptions<SummaryTokenOptions> options)
    {
        _ctx = ctx;
        _options = options.Value;
    }

    public async Task<WorkSummaryTokenGrantResponse> GrantAsync(
        WorkSummaryTokenGrantRequest request,
        MeResponse issuer,
        CancellationToken ct)
    {
        EnsureActor(issuer);
        request ??= new WorkSummaryTokenGrantRequest();

        var ownerUserId = NormalizeRequired(request.OwnerUserId, "ownerUserId");
        var units = request.Units;
        if (units <= 0 || units > MaxGrantUnits)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_SUMMARY_TOKEN_GRANT_UNITS_INVALID,
                new { request.Units, min = 1, max = MaxGrantUnits });
        }

        var tokenKind = NormalizeTokenKind(request.TokenKind);
        var monthKey = NormalizeMonthKey(request.PeriodMonthKey);
        var owner = await LoadUserAsync(ownerUserId, ct);
        await EnsureCanGrantToOwnerAsync(issuer, owner, ct);

        var now = DateTime.UtcNow;
        var grantedBefore = await CountMonthlyUnitsAsync(
            owner.Id,
            monthKey,
            tokenKind,
            WorkSummaryTokenDirections.Grant,
            WorkSummaryTokenOutcomes.Success,
            ct);
        var used = await CountMonthlyUnitsAsync(
            owner.Id,
            monthKey,
            tokenKind,
            WorkSummaryTokenDirections.Consume,
            WorkSummaryTokenOutcomes.Success,
            ct);
        var baseQuota = BaseMonthlyQuota();
        var quota = BuildQuota(owner.Id, tokenKind, monthKey, baseQuota, grantedBefore + units, used);

        var ledger = new WorkSummaryTokenLedger
        {
            Id = ObjectId.GenerateNewId().ToString(),
            OwnerUserId = owner.Id,
            ActorUserId = issuer.Id,
            IssuerUserId = issuer.Id,
            TokenKind = tokenKind,
            Direction = WorkSummaryTokenDirections.Grant,
            Units = units,
            MonthlyQuota = quota.MonthlyQuota,
            PeriodMonthKey = monthKey,
            Reason = NormalizeReason(request.Reason, "ADMIN_GRANT"),
            Outcome = WorkSummaryTokenOutcomes.Success,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = issuer.Id,
            UpdatedByUserId = issuer.Id,
            IsDeleted = false
        };

        await _ctx.WorkSummaryTokenLedgers.InsertOneAsync(ledger, cancellationToken: ct);

        return new WorkSummaryTokenGrantResponse
        {
            LedgerId = ledger.Id,
            OwnerUserId = owner.Id,
            IssuerUserId = issuer.Id,
            TokenKind = tokenKind,
            PeriodMonthKey = monthKey,
            Units = units,
            Quota = quota,
            CreatedAtUtc = now
        };
    }

    public async Task<WorkSummaryTokenQuotaResponse> GetQuotaAsync(
        string ownerUserId,
        string? tokenKind,
        string? periodMonthKey,
        MeResponse actor,
        CancellationToken ct)
    {
        EnsureActor(actor);
        var ownerId = string.IsNullOrWhiteSpace(ownerUserId) ? actor.Id : ownerUserId.Trim();
        var owner = await LoadUserAsync(ownerId, ct);
        await EnsureCanReadOwnerAsync(actor, owner, ct);

        var kind = NormalizeTokenKind(tokenKind);
        var monthKey = NormalizeMonthKey(periodMonthKey);
        return await BuildQuotaAsync(owner.Id, kind, monthKey, ct);
    }

    public async Task<PagedResult<WorkSummaryTokenLedgerRow>> SearchLedgerAsync(
        WorkSummaryTokenLedgerSearchRequest request,
        MeResponse actor,
        CancellationToken ct)
    {
        EnsureActor(actor);
        request ??= new WorkSummaryTokenLedgerSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var fb = Builders<WorkSummaryTokenLedger>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        var canReadAll = CanReadAllLedgers(actor);

        var ownerUserId = NormalizeOptionalText(request.OwnerUserId);
        if (ownerUserId is not null)
        {
            var owner = await LoadUserAsync(ownerUserId, ct);
            await EnsureCanReadOwnerAsync(actor, owner, ct);
            filter &= fb.Eq(x => x.OwnerUserId, owner.Id);
        }
        else if (!canReadAll)
        {
            filter &= fb.Or(
                fb.Eq(x => x.OwnerUserId, actor.Id),
                fb.Eq(x => x.ActorUserId, actor.Id),
                fb.Eq(x => x.IssuerUserId, actor.Id));
        }

        filter &= EqIfNotBlank(fb, x => x.ActorUserId, request.ActorUserId);
        filter &= EqIfNotBlank(fb, x => x.IssuerUserId, request.IssuerUserId);
        filter &= EqIfNotBlank(fb, x => x.PeriodMonthKey, request.PeriodMonthKey);
        filter &= EqIfNotBlank(fb, x => x.Direction, NormalizeOptionalUpper(request.Direction));
        filter &= EqIfNotBlank(fb, x => x.Outcome, NormalizeOptionalUpper(request.Outcome));
        filter &= EqIfNotBlank(fb, x => x.ConfigId, request.ConfigId);
        filter &= EqIfNotBlank(fb, x => x.JobId, request.JobId);

        var tokenKind = NormalizeOptionalText(request.TokenKind);
        if (tokenKind is not null)
            filter &= fb.Eq(x => x.TokenKind, NormalizeTokenKind(tokenKind));

        var query = NormalizeOptionalText(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Id, regex),
                fb.Regex(x => x.RequestTokenId, regex),
                fb.Regex(x => x.ConfigHash, regex),
                fb.Regex(x => x.JobId, regex),
                fb.Regex(x => x.Reason, regex),
                fb.Regex(x => x.Error, regex));
        }

        var total = await _ctx.WorkSummaryTokenLedgers.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkSummaryTokenLedgers
            .Find(filter)
            .Sort(Builders<WorkSummaryTokenLedger>.Sort.Descending(x => x.CreatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<WorkSummaryTokenLedgerRow>(
            rows.Select(ToRow).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<WorkSummaryTokenConsumeResult> ConsumeAdvancedConfigLockAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        long existingLockedConfigCount,
        string actorUserId,
        string? requestTokenId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthKey = ToMonthKey(now);
        var tokenKind = WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
        var isFree = existingLockedConfigCount <= 0;
        var units = isFree ? 0 : 1;
        var quota = await BuildQuotaAsync(actorUserId, tokenKind, monthKey, ct);
        var usedBefore = quota.UsedUnits;

        if (WouldExceedQuota(usedBefore, units, quota.MonthlyQuota))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_SUMMARY_TOKEN_QUOTA_EXCEEDED,
                new
                {
                    actorUserId,
                    monthKey,
                    monthlyQuota = quota.MonthlyQuota,
                    baseMonthlyQuota = quota.BaseMonthlyQuota,
                    grantedUnits = quota.GrantedUnits,
                    usedBefore,
                    requestedUnits = units,
                    tokenKind,
                    configId = config.Id,
                    config.WorkId,
                    config.AssignmentId,
                    config.DynamicFormTemplateId,
                    config.SectionId
                });
        }

        var ledger = new WorkSummaryTokenLedger
        {
            Id = ObjectId.GenerateNewId().ToString(),
            OwnerUserId = actorUserId,
            ActorUserId = actorUserId,
            TokenKind = tokenKind,
            Direction = isFree ? WorkSummaryTokenDirections.Free : WorkSummaryTokenDirections.Consume,
            Units = units,
            MonthlyQuota = quota.MonthlyQuota,
            PeriodMonthKey = monthKey,
            RequestTokenId = NormalizeOptionalText(requestTokenId),
            WorkId = config.WorkId,
            WorkAssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            ConfigId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigHash = config.ConfigHash,
            Reason = isFree
                ? "INITIAL_ADVANCED_SUMMARY_CONFIG_LOCK"
                : "CHANGE_ADVANCED_SUMMARY_CONFIG_LOCK",
            Outcome = WorkSummaryTokenOutcomes.Success,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        await _ctx.WorkSummaryTokenLedgers.InsertOneAsync(ledger, cancellationToken: ct);

        return new WorkSummaryTokenConsumeResult(
            ledger.Id,
            units,
            quota.MonthlyQuota,
            usedBefore,
            usedBefore + units,
            isFree);
    }

    public async Task MarkFailedAsync(
        string ledgerId,
        string actorUserId,
        string error,
        CancellationToken ct)
    {
        ledgerId = ledgerId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ledgerId))
            return;

        var now = DateTime.UtcNow;
        var update = Builders<WorkSummaryTokenLedger>.Update
            .Set(x => x.Outcome, WorkSummaryTokenOutcomes.Failed)
            .Set(x => x.Error, NormalizeError(error))
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.WorkSummaryTokenLedgers.UpdateOneAsync(
            x => x.Id == ledgerId && !x.IsDeleted,
            update,
            cancellationToken: ct);
    }

    private async Task<WorkSummaryTokenQuotaResponse> BuildQuotaAsync(
        string ownerUserId,
        string tokenKind,
        string monthKey,
        CancellationToken ct)
    {
        var baseQuota = BaseMonthlyQuota();
        var granted = await CountMonthlyUnitsAsync(
            ownerUserId,
            monthKey,
            tokenKind,
            WorkSummaryTokenDirections.Grant,
            WorkSummaryTokenOutcomes.Success,
            ct);
        var used = await CountMonthlyUnitsAsync(
            ownerUserId,
            monthKey,
            tokenKind,
            WorkSummaryTokenDirections.Consume,
            WorkSummaryTokenOutcomes.Success,
            ct);

        return BuildQuota(ownerUserId, tokenKind, monthKey, baseQuota, granted, used);
    }

    private async Task<int> CountMonthlyUnitsAsync(
        string ownerUserId,
        string monthKey,
        string tokenKind,
        string direction,
        string outcome,
        CancellationToken ct)
    {
        var fb = Builders<WorkSummaryTokenLedger>.Filter;
        var filter = fb.Eq(x => x.OwnerUserId, ownerUserId)
                     & fb.Eq(x => x.PeriodMonthKey, monthKey)
                     & fb.Eq(x => x.TokenKind, tokenKind)
                     & fb.Eq(x => x.Direction, direction)
                     & fb.Eq(x => x.Outcome, outcome)
                     & fb.Eq(x => x.IsDeleted, false);

        var rows = await _ctx.WorkSummaryTokenLedgers
            .Find(filter)
            .Project(x => x.Units)
            .ToListAsync(ct);

        return rows.Sum();
    }

    private async Task<AppUser> LoadUserAsync(string userId, CancellationToken ct)
    {
        var id = NormalizeRequired(userId, "userId");
        return await _ctx.Users
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { userId = id });
    }

    private async Task<Unit?> LoadUnitAsync(string? unitId, CancellationToken ct)
    {
        unitId = NormalizeOptionalText(unitId);
        if (unitId is null)
            return null;

        return await _ctx.Units
            .Find(x => x.Id == unitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
    }

    private async Task EnsureCanReadOwnerAsync(MeResponse actor, AppUser owner, CancellationToken ct)
    {
        if (string.Equals(actor.Id, owner.Id, StringComparison.Ordinal))
            return;

        if (CanReadAllLedgers(actor))
            return;

        if (await IsOwnerInManagementScopeAsync(actor, owner, ct))
            return;

        throw GrantForbidden("readOwnerOutsideScope", actor.Id, owner.Id);
    }

    private async Task EnsureCanGrantToOwnerAsync(MeResponse issuer, AppUser owner, CancellationToken ct)
    {
        if (string.Equals(issuer.Id, owner.Id, StringComparison.Ordinal))
            throw GrantForbidden("selfGrant", issuer.Id, owner.Id);

        if (CanReadAllLedgers(issuer))
            return;

        if (IsPrivilegedUser(owner))
            throw GrantForbidden("targetPrivileged", issuer.Id, owner.Id);

        if (await IsOwnerInManagementScopeAsync(issuer, owner, ct))
            return;

        throw GrantForbidden("targetOutsideScope", issuer.Id, owner.Id);
    }

    private async Task<bool> IsOwnerInManagementScopeAsync(
        MeResponse actor,
        AppUser owner,
        CancellationToken ct)
    {
        var ownerUnit = await LoadUnitAsync(owner.UnitId, ct);
        if (ownerUnit is null)
            return false;

        if (RoleGuard.TryGetManagerUnit(actor, out var managedUnitId))
        {
            var scopeUnit = await LoadUnitAsync(managedUnitId, ct);
            return scopeUnit is not null && IsSameOrDescendantUnit(scopeUnit, ownerUnit);
        }

        if (RoleGuard.IsManagerLevel(actor))
        {
            var scope = await ResolveManagerLevelScopeAsync(actor, ct);
            if (scope.IsLevelWide)
                return ownerUnit.Level >= scope.Level;

            return scope.Unit is not null &&
                   ownerUnit.Level >= scope.Unit.Level &&
                   IsSameOrDescendantUnit(scope.Unit, ownerUnit);
        }

        return false;
    }

    private async Task<ManagerLevelScope> ResolveManagerLevelScopeAsync(
        MeResponse actor,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(actor.UnitId))
        {
            var unit = await LoadUnitAsync(actor.UnitId, ct);
            if (unit is not null)
                return new ManagerLevelScope(unit, unit.Level, IsLevelWide: false);
        }

        if (RoleGuard.TryGetGeneratedLevelManager(actor, out var level))
            return new ManagerLevelScope(Unit: null, level, IsLevelWide: true);

        return new ManagerLevelScope(Unit: null, Level: int.MaxValue, IsLevelWide: true);
    }

    private static FilterDefinition<WorkSummaryTokenLedger> EqIfNotBlank(
        FilterDefinitionBuilder<WorkSummaryTokenLedger> fb,
        System.Linq.Expressions.Expression<Func<WorkSummaryTokenLedger, string?>> field,
        string? value)
    {
        value = NormalizeOptionalText(value);
        return value is null ? FilterDefinition<WorkSummaryTokenLedger>.Empty : fb.Eq(field, value);
    }

    private static WorkSummaryTokenQuotaResponse BuildQuota(
        string ownerUserId,
        string tokenKind,
        string monthKey,
        int baseMonthlyQuota,
        int grantedUnits,
        int usedUnits)
    {
        var monthlyQuota = CalculateAllowance(baseMonthlyQuota, grantedUnits);
        return new WorkSummaryTokenQuotaResponse
        {
            OwnerUserId = ownerUserId,
            TokenKind = tokenKind,
            PeriodMonthKey = monthKey,
            BaseMonthlyQuota = Math.Max(0, baseMonthlyQuota),
            GrantedUnits = Math.Max(0, grantedUnits),
            UsedUnits = Math.Max(0, usedUnits),
            MonthlyQuota = monthlyQuota,
            RemainingUnits = Math.Max(0, monthlyQuota - Math.Max(0, usedUnits))
        };
    }

    private static WorkSummaryTokenLedgerRow ToRow(WorkSummaryTokenLedger x)
        => new()
        {
            Id = x.Id,
            OwnerUserId = x.OwnerUserId,
            ActorUserId = x.ActorUserId,
            IssuerUserId = x.IssuerUserId,
            TokenKind = x.TokenKind,
            Direction = x.Direction,
            Units = x.Units,
            MonthlyQuota = x.MonthlyQuota,
            PeriodMonthKey = x.PeriodMonthKey,
            RequestTokenId = x.RequestTokenId,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            SectionId = x.SectionId,
            ConfigId = x.ConfigId,
            ConfigVersionNo = x.ConfigVersionNo,
            ConfigHash = x.ConfigHash,
            JobId = x.JobId,
            Reason = x.Reason,
            Outcome = x.Outcome,
            Error = x.Error,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private int BaseMonthlyQuota()
        => Math.Max(0, _options.MonthlyQuota);

    internal static string ToMonthKey(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static bool WouldExceedQuota(int usedBefore, int requestedUnits, int monthlyQuota)
        => requestedUnits > 0 && usedBefore + requestedUnits > Math.Max(0, monthlyQuota);

    internal static int CalculateAllowance(int baseMonthlyQuota, int grantedUnits)
        => Math.Max(0, baseMonthlyQuota) + Math.Max(0, grantedUnits);

    internal static bool IsSameOrDescendantUnit(Unit scopeUnit, Unit targetUnit)
    {
        if (string.Equals(scopeUnit.Id, targetUnit.Id, StringComparison.Ordinal))
            return true;

        var scopeCode = scopeUnit.Code?.Trim();
        var targetCode = targetUnit.Code?.Trim();
        if (!string.IsNullOrWhiteSpace(scopeCode) &&
            !string.IsNullOrWhiteSpace(targetCode) &&
            targetUnit.Level >= scopeUnit.Level &&
            targetCode.StartsWith(scopeCode, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(targetUnit.ParentUnitId, scopeUnit.Id, StringComparison.Ordinal);
    }

    private static bool CanReadAllLedgers(MeResponse actor)
        => RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor);

    private static bool IsPrivilegedUser(AppUser user)
        => user.Roles?.Any(x =>
               string.Equals(x, Roles.ADMIN, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(x, Roles.SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase)) == true ||
           string.Equals(user.AccountKind, ManagementAccountKind.SystemAdmin, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTokenKind(string? value)
    {
        var text = NormalizeOptionalText(value) ?? WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
        if (string.Equals(text, WorkSummaryTokenKinds.AdvancedSummaryConfigLock, StringComparison.OrdinalIgnoreCase))
            return WorkSummaryTokenKinds.AdvancedSummaryConfigLock;
        if (string.Equals(text, WorkSummaryTokenKinds.AdvancedSummaryBroadHistoricalBuild, StringComparison.OrdinalIgnoreCase))
            return WorkSummaryTokenKinds.AdvancedSummaryBroadHistoricalBuild;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_SUMMARY_TOKEN_KIND_INVALID,
            new { tokenKind = value });
    }

    private static string NormalizeMonthKey(string? value)
    {
        var text = NormalizeOptionalText(value);
        if (text is null)
            return ToMonthKey(DateTime.UtcNow);

        if (!MonthKeyRegex.IsMatch(text))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "periodMonthKey", value, expected = "yyyy-MM" });
        }

        return text;
    }

    private static string NormalizeRequired(string? value, string field)
    {
        var text = NormalizeOptionalText(value);
        if (text is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field });

        return text;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? NormalizeOptionalUpper(string? value)
        => NormalizeOptionalText(value)?.ToUpperInvariant();

    private static string NormalizeReason(string? value, string fallback)
    {
        var text = NormalizeOptionalText(value) ?? fallback;
        return text.Length <= 500 ? text : text[..500];
    }

    private static string NormalizeError(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "UNKNOWN_TOKEN_CONSUME_FAILURE";

        return text.Length <= 1000 ? text : text[..1000];
    }

    private static void EnsureActor(MeResponse actor)
    {
        if (actor is null || string.IsNullOrWhiteSpace(actor.Id))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.AUTH_ME_NOT_AVAILABLE);
    }

    private static AppException GrantForbidden(string reason, string? actorUserId, string? ownerUserId)
        => AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_SUMMARY_TOKEN_GRANT_FORBIDDEN,
            new { reason, actorUserId, ownerUserId });

    private sealed record ManagerLevelScope(Unit? Unit, int Level, bool IsLevelWide);
}
