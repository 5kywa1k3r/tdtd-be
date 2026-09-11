using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public sealed partial class WorkSummaryTokenService : IWorkSummaryTokenService
{
    private const int MaxGrantUnits = 1000;
    private static readonly Regex MonthKeyRegex = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    private readonly MongoDbContext _ctx;
    private readonly IStatConfigTransactionRunner _transactions;

    public WorkSummaryTokenService(
        MongoDbContext ctx,
        IStatConfigTransactionRunner transactions)
    {
        _ctx = ctx;
        _transactions = transactions;
    }

    public Task<WorkSummaryTokenGrantResponse> GrantAsync(
        WorkSummaryTokenGrantRequest request,
        MeResponse issuer,
        CancellationToken ct)
        => throw P805LegacyMutationBlocked(
            "WORK_SUMMARY_TOKEN_LEGACY_GRANT_BLOCKED",
            "GrantP8Async");

    public async Task<WorkSummaryTokenQuotaResponse> GetQuotaAsync(
        string ownerUnitId,
        string? tokenKind,
        string? periodMonthKey,
        MeResponse actor,
        CancellationToken ct)
    {
        EnsureActor(actor);
        var unitId = string.IsNullOrWhiteSpace(ownerUnitId)
            ? NormalizeRequired(actor.UnitId, "ownerUnitId")
            : ownerUnitId.Trim();
        var ownerUnit = await LoadUnitRequiredAsync(unitId, ct);
        await EnsureCanReadUnitPoolAsync(actor, ownerUnit, ct);

        var kind = NormalizeTokenKind(tokenKind);
        var monthKey = NormalizeMonthKey(periodMonthKey);
        return await BuildQuotaP8ReadAsync(ownerUnit.Id, kind, monthKey, ct);
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
        var filter =
            fb.Eq(x => x.IsDeleted, false) &
            fb.Ne(x => x.RecordKind, WorkSummaryTokenLedgerRecordKinds.Pool);
        var canReadAll = CanReadAllLedgers(actor);

        var ownerUnitId = NormalizeOptionalText(request.OwnerUnitId);
        if (ownerUnitId is not null)
        {
            var ownerUnit = await LoadUnitRequiredAsync(ownerUnitId, ct);
            await EnsureCanReadUnitPoolAsync(actor, ownerUnit, ct);
            filter &= fb.Eq(x => x.OwnerUnitId, ownerUnit.Id);
        }
        else if (!canReadAll)
        {
            var actorUnitId = NormalizeRequired(actor.UnitId, "ownerUnitId");
            filter &= fb.Eq(x => x.OwnerUnitId, actorUnitId);
        }

        filter &= EqIfNotBlank(fb, x => x.OwnerUserId, request.OwnerUserId);
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

    public Task<WorkSummaryTokenConsumeResult> ConsumeAdvancedConfigLockAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        long existingLockedConfigCount,
        string actorUserId,
        string? requestTokenId,
        CancellationToken ct)
        => throw P805LegacyMutationBlocked(
            "WORK_SUMMARY_TOKEN_LEGACY_CONSUME_BLOCKED",
            "ConsumeAdvancedConfigLockP8Async");

    public Task MarkFailedAsync(
        string ledgerId,
        string actorUserId,
        string error,
        CancellationToken ct)
        => throw P805LegacyMutationBlocked(
            "WORK_SUMMARY_TOKEN_LEGACY_MARK_FAILED_BLOCKED",
            "CompensateP8Async");

    private static AppException P805LegacyMutationBlocked(
        string reason,
        string replacement)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new
            {
                path = "$.operation",
                reason,
                replacement
            });

    private async Task<WorkSummaryTokenQuotaResponse> BuildQuotaAsync(
        string ownerUnitId,
        string tokenKind,
        string monthKey,
        CancellationToken ct)
    {
        var baseQuota = await CountActiveUsersInUnitAsync(ownerUnitId, ct);
        var granted = await CountMonthlyUnitsAsync(
            ownerUnitId,
            monthKey,
            tokenKind,
            WorkSummaryTokenDirections.Grant,
            WorkSummaryTokenOutcomes.Success,
            ct);
        var used = await CountMonthlyUnitsAsync(
            ownerUnitId,
            monthKey,
            tokenKind,
            WorkSummaryTokenDirections.Consume,
            WorkSummaryTokenOutcomes.Success,
            ct);

        return BuildQuota(ownerUnitId, tokenKind, monthKey, baseQuota, granted, used);
    }

    private async Task<int> CountMonthlyUnitsAsync(
        string ownerUnitId,
        string monthKey,
        string tokenKind,
        string direction,
        string outcome,
        CancellationToken ct)
    {
        var fb = Builders<WorkSummaryTokenLedger>.Filter;
        var filter = fb.Eq(x => x.OwnerUnitId, ownerUnitId)
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

    private async Task<Unit> LoadUnitRequiredAsync(string unitId, CancellationToken ct)
    {
        var id = NormalizeRequired(unitId, "ownerUnitId");
        return await LoadUnitAsync(id, ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.COMMON_NOT_FOUND, new { ownerUnitId = id });
    }

    private static void EnsureCanGrantExtraQuota(MeResponse issuer, Unit ownerUnit)
    {
        if (CanGrantExtraQuota(issuer))
            return;

        throw GrantForbidden("adminRequiredForExtraGrant", issuer.Id, ownerUnit.Id);
    }

    private async Task EnsureCanReadUnitPoolAsync(MeResponse actor, Unit ownerUnit, CancellationToken ct)
    {
        if (CanReadAllLedgers(actor))
            return;

        if (!string.IsNullOrWhiteSpace(actor.UnitId) &&
            string.Equals(actor.UnitId, ownerUnit.Id, StringComparison.Ordinal))
            return;

        if (await IsUnitInManagementScopeAsync(actor, ownerUnit, ct))
            return;

        throw GrantForbidden("readUnitOutsideScope", actor.Id, ownerUnit.Id);
    }

    private async Task<bool> IsUnitInManagementScopeAsync(
        MeResponse actor,
        Unit ownerUnit,
        CancellationToken ct)
    {
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

    private async Task<int> CountActiveUsersInUnitAsync(string unitId, CancellationToken ct)
    {
        var activeUsers = await _ctx.Users.CountDocumentsAsync(
            x => x.UnitId == unitId && !x.IsDeleted,
            cancellationToken: ct);
        return CalculateBaseQuotaFromActiveUsers((int)activeUsers);
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
        string ownerUnitId,
        string tokenKind,
        string monthKey,
        int baseMonthlyQuota,
        int grantedUnits,
        int usedUnits)
    {
        var monthlyQuota = CalculateAllowance(baseMonthlyQuota, grantedUnits);
        return new WorkSummaryTokenQuotaResponse
        {
            OwnerUnitId = ownerUnitId,
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
            RecordKind = string.IsNullOrWhiteSpace(x.RecordKind)
                ? WorkSummaryTokenLedgerRecordKinds.Entry : x.RecordKind,
            OwnerUserId = x.OwnerUserId,
            OwnerUnitId = x.OwnerUnitId,
            ActorUserId = x.ActorUserId,
            IssuerUserId = x.IssuerUserId,
            TokenKind = x.TokenKind,
            Direction = x.Direction,
            Units = x.Units,
            MonthlyQuota = x.MonthlyQuota,
            BaseMonthlyQuota = x.BaseMonthlyQuota,
            GrantedUnits = x.GrantedUnits,
            UsedUnits = x.UsedUnits,
            Revision = x.Revision,
            PeriodMonthKey = x.PeriodMonthKey,
            RequestTokenId = x.RequestTokenId,
            RequestHash = x.RequestHash,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            SectionId = x.SectionId,
            ConfigId = x.ConfigId,
            ConfigVersionNo = x.ConfigVersionNo,
            ConfigHash = x.ConfigHash,
            PoolId = x.PoolId,
            PoolRevisionBefore = x.PoolRevisionBefore,
            PoolRevisionAfter = x.PoolRevisionAfter,
            PoolHashBefore = x.PoolHashBefore,
            PoolHashAfter = x.PoolHashAfter,
            CommandReceiptId = x.CommandReceiptId,
            CompensatesLedgerId = x.CompensatesLedgerId,
            JobId = x.JobId,
            Reason = x.Reason,
            Outcome = x.Outcome,
            Error = x.Error,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    internal static string ToMonthKey(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static bool WouldExceedQuota(int usedBefore, int requestedUnits, int monthlyQuota)
        => requestedUnits > 0 && usedBefore + requestedUnits > Math.Max(0, monthlyQuota);

    internal static int CalculateAllowance(int baseMonthlyQuota, int grantedUnits)
        => Math.Max(0, baseMonthlyQuota) + Math.Max(0, grantedUnits);

    internal static int CalculateBaseQuotaFromActiveUsers(int activeUserCount)
        => Math.Max(0, activeUserCount);

    internal static bool CanGrantExtraQuota(MeResponse actor)
        => RoleGuard.IsAdmin(actor) || RoleGuard.IsSystemAdmin(actor);

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

    private static AppException GrantForbidden(string reason, string? actorUserId, string? ownerUnitId)
        => AppExceptionFactory.Forbidden(
            AppErrorCode.WORK_SUMMARY_TOKEN_GRANT_FORBIDDEN,
            new { reason, actorUserId, ownerUnitId });

    private sealed record ManagerLevelScope(Unit? Unit, int Level, bool IsLevelWide);
}
