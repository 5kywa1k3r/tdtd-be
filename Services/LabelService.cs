using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.Jobs;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.Services;

public interface ILabelService
{
    Task<PagedResult<LabelRow>> SearchAsync(LabelSearchReq req, CancellationToken ct);
    Task<LabelRow> GetByIdAsync(string id, CancellationToken ct);
    Task<LabelRow> CreateAsync(CreateLabelReq req, CancellationToken ct);
    Task<LabelRow> UpdateAsync(string id, UpdateLabelReq req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

public sealed class LabelService : ILabelService
{
    private static readonly Regex CodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HexColorRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IWorkReportStatisticRebuildJobService _statisticRebuildJobs;
    private readonly ILabelEnumCatalogService _enumCatalogs;

    public LabelService(
        MongoDbContext ctx,
        MeAccessor me,
        IWorkReportStatisticRebuildJobService statisticRebuildJobs,
        ILabelEnumCatalogService enumCatalogs)
    {
        _ctx = ctx;
        _me = me;
        _statisticRebuildJobs = statisticRebuildJobs;
        _enumCatalogs = enumCatalogs;
    }

    private static AppException LabelRequestRequired(string action)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.LABEL_REQUEST_REQUIRED,
            new { action });

    private static AppException LabelIdRequired(string? id)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.LABEL_ID_REQUIRED,
            new { id });

    private static AppException LabelNotFound(string? id)
        => AppExceptionFactory.NotFound(
            AppErrorCode.LABEL_NOT_FOUND,
            new { id });

    private static object LabelDetails(LabelCatalogItem doc, string? actorUserId = null)
        => new
        {
            labelId = doc.Id,
            doc.Code,
            doc.ScopeType,
            doc.ScopeId,
            doc.IsSystem,
            actorUserId
        };

    private static object UserDetails(MeResponse me)
        => new
        {
            userId = me.Id,
            me.AccountKind,
            me.Roles,
            me.UnitId
        };

    private static AppException LabelValidation(
        AppErrorCode code,
        string reason,
        object? details = null)
        => AppExceptionFactory.BadRequest(
            code,
            new
            {
                reason,
                details
            });

    public async Task<PagedResult<LabelRow>> SearchAsync(LabelSearchReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        req ??= new LabelSearchReq(null, null, null, null, null, null, null, null);

        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var fb = Builders<LabelCatalogItem>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false) & BuildVisibilityFilter(me);

        if (!IsLabelManager(me))
            filter &= fb.Eq(x => x.IsActive, true);
        else if (req.IsActive.HasValue)
            filter &= fb.Eq(x => x.IsActive, req.IsActive.Value);

        if (!string.IsNullOrWhiteSpace(req.Code))
            filter &= fb.Regex(x => x.Code, new BsonRegularExpression(Regex.Escape(req.Code.Trim()), "i"));

        if (!string.IsNullOrWhiteSpace(req.Name))
            filter &= fb.Regex(x => x.NameLower, new BsonRegularExpression(Regex.Escape(req.Name.Trim().ToLowerInvariant()), "i"));

        if (!string.IsNullOrWhiteSpace(req.GroupCode))
            filter &= fb.Eq(x => x.GroupCode, NormalizeOptionalCode(req.GroupCode));

        if (!string.IsNullOrWhiteSpace(req.Usage))
            filter &= BuildUsageSearchFilter(NormalizeUsage(req.Usage, allowGenericFallback: false));

        if (!string.IsNullOrWhiteSpace(req.ScopeType))
            filter &= fb.Eq(x => x.ScopeType, NormalizeScopeType(req.ScopeType));

        if (!string.IsNullOrWhiteSpace(req.ScopeId))
            filter &= fb.Eq(x => x.ScopeId, req.ScopeId.Trim());

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = Regex.Escape(req.Q.Trim());
            var rx = new BsonRegularExpression(q, "i");
            filter &= fb.Or(fb.Regex(x => x.Code, rx), fb.Regex(x => x.Name, rx), fb.Regex(x => x.NameLower, rx));
        }

        var total = await _ctx.Labels.CountDocumentsAsync(filter, cancellationToken: ct);
        var docs = await _ctx.Labels
            .Find(filter)
            .Sort(BuildSort(req.SortField, req.SortDirection))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var rows = docs.Select(x => ToRow(x, me)).ToList();
        return new PagedResult<LabelRow>(rows, total, page, pageSize);
    }

    public async Task<LabelRow> GetByIdAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadVisibleAsync(id, me, ct);
        return ToRow(doc, me);
    }

    public async Task<LabelRow> CreateAsync(CreateLabelReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireLabelManager(me);
        if (req is null)
            throw LabelRequestRequired("create");

        var scope = ResolveManagedScope(me, req.ScopeType, req.ScopeId);
        var name = NormalizeName(req.Name);
        var now = DateTime.UtcNow;
        var valueSourceType = NormalizeValueSourceType(req.ValueSourceType, req.Usage, req.DataType);
        var valueSourceCatalog = valueSourceType == LabelValueSourceTypes.EnumCatalog
            ? await _enumCatalogs.EnsureVisibleActiveCatalogAsync(req.ValueSourceCatalogId ?? string.Empty, ct)
            : null;
        var doc = new LabelCatalogItem
        {
            Code = NormalizeCode(req.Code),
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Description = NormalizeOptionalText(req.Description),
            Color = NormalizeColor(req.Color),
            GroupCode = NormalizeOptionalCode(req.GroupCode),
            Usage = NormalizeUsage(req.Usage),
            DataType = LabelDataTypes.Normalize(req.DataType),
            ValueSourceType = valueSourceType,
            ValueOptions = NormalizeValueOptions(req.ValueOptions, valueSourceType),
            ValueSourceCatalogId = valueSourceCatalog?.Id,
            ValueSourceCatalogCode = valueSourceCatalog?.Code,
            ValueSourceCatalogName = valueSourceCatalog?.Name,
            ScopeType = scope.scopeType,
            ScopeId = scope.scopeId,
            ManagedByUserId = me.Id,
            IsSystem = false,
            IsActive = req.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false
        };

        try
        {
            await _ctx.Labels.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.LABEL_DUPLICATE_CODE,
                new { doc.Code, doc.ScopeType, doc.ScopeId });
        }

        return ToRow(doc, me);
    }

    public async Task<LabelRow> UpdateAsync(string id, UpdateLabelReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireLabelManager(me);
        if (req is null)
            throw LabelRequestRequired("update");

        var doc = await LoadVisibleAsync(id, me, ct);
        EnsureCanManage(me, doc);
        if (doc.IsSystem)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_SYSTEM_UPDATE_FORBIDDEN,
                LabelDetails(doc, me.Id));

        var name = NormalizeName(req.Name);
        var nextDataType = LabelDataTypes.Normalize(req.DataType);
        var nextUsage = NormalizeUsage(req.Usage, LabelUsages.Normalize(doc.Usage, LabelUsages.Classification));
        var nextValueSourceType = NormalizeValueSourceType(req.ValueSourceType, nextUsage, nextDataType);
        var nextValueOptions = NormalizeValueOptions(req.ValueOptions, nextValueSourceType);
        var nextValueSourceCatalog = nextValueSourceType == LabelValueSourceTypes.EnumCatalog
            ? await _enumCatalogs.EnsureVisibleActiveCatalogAsync(req.ValueSourceCatalogId ?? string.Empty, ct)
            : null;
        var now = DateTime.UtcNow;
        var update = Builders<LabelCatalogItem>.Update
            .Set(x => x.Name, name)
            .Set(x => x.NameLower, name.ToLowerInvariant())
            .Set(x => x.Description, NormalizeOptionalText(req.Description))
            .Set(x => x.Color, NormalizeColor(req.Color))
            .Set(x => x.GroupCode, NormalizeOptionalCode(req.GroupCode))
            .Set(x => x.Usage, nextUsage)
            .Set(x => x.DataType, nextDataType)
            .Set(x => x.ValueSourceType, nextValueSourceType)
            .Set(x => x.ValueOptions, nextValueOptions)
            .Set(x => x.ValueSourceCatalogId, nextValueSourceCatalog?.Id)
            .Set(x => x.ValueSourceCatalogCode, nextValueSourceCatalog?.Code)
            .Set(x => x.ValueSourceCatalogName, nextValueSourceCatalog?.Name)
            .Set(x => x.IsActive, req.IsActive)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        await _ctx.Labels.UpdateOneAsync(x => x.Id == id && !x.IsDeleted, update, cancellationToken: ct);
        if (doc.IsActive != req.IsActive ||
            !string.Equals(LabelUsages.Normalize(doc.Usage, LabelUsages.Classification), nextUsage, StringComparison.Ordinal) ||
            !string.Equals(LabelDataTypes.Normalize(doc.DataType), nextDataType, StringComparison.Ordinal))
        {
            await EnqueueLabelStatisticRebuildAsync(doc, me.Id, ct);
        }

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireLabelManager(me);

        var doc = await LoadVisibleAsync(id, me, ct);
        EnsureCanManage(me, doc);
        if (doc.IsSystem)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_SYSTEM_DELETE_FORBIDDEN,
                LabelDetails(doc, me.Id));

        var now = DateTime.UtcNow;
        await _ctx.Labels.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<LabelCatalogItem>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.IsActive, false)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, me.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        await EnqueueLabelStatisticRebuildAsync(doc, me.Id, ct);
    }

    private async Task EnqueueLabelStatisticRebuildAsync(
        LabelCatalogItem label,
        string actorUserId,
        CancellationToken ct)
    {
        var results = await _statisticRebuildJobs.EnqueueForLabelChangeAsync(
            label,
            actorUserId,
            highPriority: true,
            ct);

        if (results.Count > 0)
            HangfireRecurringJobRegistrar.TriggerDynamicFormStatisticRebuildNow();
    }

    private async Task<LabelCatalogItem> LoadVisibleAsync(string id, MeResponse me, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw LabelIdRequired(id);

        var filter = Builders<LabelCatalogItem>.Filter.Eq(x => x.Id, id.Trim())
                     & Builders<LabelCatalogItem>.Filter.Eq(x => x.IsDeleted, false)
                     & BuildVisibilityFilter(me);

        var doc = await _ctx.Labels.Find(filter).FirstOrDefaultAsync(ct);
        if (doc is null)
            throw LabelNotFound(id);

        return doc;
    }

    private static FilterDefinition<LabelCatalogItem> BuildVisibilityFilter(MeResponse me)
    {
        var fb = Builders<LabelCatalogItem>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return FilterDefinition<LabelCatalogItem>.Empty;

        var scopes = new List<FilterDefinition<LabelCatalogItem>>
        {
            fb.Eq(x => x.ScopeType, LabelScopeTypes.Global)
        };

        if (!string.IsNullOrWhiteSpace(me.UnitId))
            scopes.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Unit) & fb.Eq(x => x.ScopeId, me.UnitId));

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
            scopes.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Unit) & fb.Eq(x => x.ScopeId, managedUnitId));

        if (RoleGuard.IsManagerLevel(me) && !string.IsNullOrWhiteSpace(me.UnitId))
            scopes.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Level) & fb.Eq(x => x.ScopeId, me.UnitId));

        return fb.Or(scopes);
    }

    private static bool IsLabelManager(MeResponse me)
        => RoleGuard.IsSystemAdmin(me) || RoleGuard.IsManagerLevel(me) || RoleGuard.TryGetManagerUnit(me, out _);

    private static void RequireLabelManager(MeResponse me)
    {
        if (!IsLabelManager(me))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_MANAGER_REQUIRED,
                UserDetails(me));
    }

    private static void EnsureCanManage(MeResponse me, LabelCatalogItem doc)
    {
        if (RoleGuard.IsSystemAdmin(me))
            return;

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId) &&
            doc.ScopeType == LabelScopeTypes.Unit &&
            string.Equals(doc.ScopeId, managedUnitId, StringComparison.Ordinal))
            return;

        if (RoleGuard.IsManagerLevel(me) &&
            doc.ScopeType == LabelScopeTypes.Level &&
            string.Equals(doc.ScopeId, me.UnitId, StringComparison.Ordinal))
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.LABEL_MANAGE_FORBIDDEN,
            LabelDetails(doc, me.Id));
    }

    private static (string scopeType, string? scopeId) ResolveManagedScope(
        MeResponse me,
        string? requestedScopeType,
        string? requestedScopeId)
    {
        if (RoleGuard.IsSystemAdmin(me))
        {
            var type = string.IsNullOrWhiteSpace(requestedScopeType)
                ? LabelScopeTypes.Global
                : NormalizeScopeType(requestedScopeType);
            var scopeId = type == LabelScopeTypes.Global ? null : NormalizeScopeId(requestedScopeId, type);
            return (type, scopeId);
        }

        if (RoleGuard.TryGetManagerUnit(me, out var unitId))
        {
            EnsureRequestedScopeMatches(requestedScopeType, requestedScopeId, LabelScopeTypes.Unit, unitId);
            return (LabelScopeTypes.Unit, unitId);
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            if (string.IsNullOrWhiteSpace(me.UnitId))
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.LABEL_SCOPE_LEVEL_UNAVAILABLE,
                    UserDetails(me));

            EnsureRequestedScopeMatches(requestedScopeType, requestedScopeId, LabelScopeTypes.Level, me.UnitId);
            return (LabelScopeTypes.Level, me.UnitId);
        }

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.LABEL_MANAGER_REQUIRED,
            UserDetails(me));
    }

    private static void EnsureRequestedScopeMatches(
        string? requestedScopeType,
        string? requestedScopeId,
        string expectedScopeType,
        string expectedScopeId)
    {
        if (!string.IsNullOrWhiteSpace(requestedScopeType) &&
            NormalizeScopeType(requestedScopeType) != expectedScopeType)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_SCOPE_MISMATCH,
                new { requestedScopeType, requestedScopeId, expectedScopeType, expectedScopeId });

        if (!string.IsNullOrWhiteSpace(requestedScopeId) &&
            !string.Equals(requestedScopeId.Trim(), expectedScopeId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_SCOPE_MISMATCH,
                new { requestedScopeType, requestedScopeId, expectedScopeType, expectedScopeId });
    }

    private static LabelRow ToRow(LabelCatalogItem x, MeResponse me)
        => new(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.Color,
            x.GroupCode,
            LabelUsages.Normalize(x.Usage, LabelUsages.Classification),
            LabelDataTypes.Normalize(x.DataType),
            LabelValueSourceTypes.Normalize(x.ValueSourceType),
            (x.ValueOptions ?? new List<LabelValueOption>())
                .Select(option => new LabelValueOptionDto(option.Code, option.Label))
                .ToList(),
            x.ValueSourceCatalogId,
            x.ValueSourceCatalogCode,
            x.ValueSourceCatalogName,
            x.ScopeType,
            x.ScopeId,
            x.IsSystem,
            x.IsActive,
            IsLabelManager(me) && CanManage(me, x),
            x.CreatedAtUtc,
            x.UpdatedAtUtc);

    private static bool CanManage(MeResponse me, LabelCatalogItem x)
    {
        if (RoleGuard.IsSystemAdmin(me))
            return true;
        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId) &&
            x.ScopeType == LabelScopeTypes.Unit &&
            x.ScopeId == managedUnitId)
            return true;
        return RoleGuard.IsManagerLevel(me) &&
               x.ScopeType == LabelScopeTypes.Level &&
               x.ScopeId == me.UnitId;
    }

    private static SortDefinition<LabelCatalogItem> BuildSort(string? field, string? direction)
    {
        var desc = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        var sort = Builders<LabelCatalogItem>.Sort;
        return (field ?? "name").ToLowerInvariant() switch
        {
            "code" => desc ? sort.Descending(x => x.Code) : sort.Ascending(x => x.Code),
            "groupcode" => desc ? sort.Descending(x => x.GroupCode) : sort.Ascending(x => x.GroupCode),
            "usage" => desc ? sort.Descending(x => x.Usage) : sort.Ascending(x => x.Usage),
            "createdatutc" => desc ? sort.Descending(x => x.CreatedAtUtc) : sort.Ascending(x => x.CreatedAtUtc),
            "updatedatutc" => desc ? sort.Descending(x => x.UpdatedAtUtc) : sort.Ascending(x => x.UpdatedAtUtc),
            _ => desc ? sort.Descending(x => x.NameLower) : sort.Ascending(x => x.NameLower)
        };
    }

    private static string NormalizeCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw LabelValidation(AppErrorCode.LABEL_CODE_REQUIRED, "Ma nhan khong duoc trong.");
        if (!CodeRegex.IsMatch(code))
            throw LabelValidation(
                AppErrorCode.LABEL_CODE_INVALID,
                "Ma nhan chi gom chu thuong, so, dau -, _ hoac . va toi da 64 ky tu.",
                new { code });
        return code;
    }

    private static string? NormalizeOptionalCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (!CodeRegex.IsMatch(code))
            throw LabelValidation(
                AppErrorCode.LABEL_GROUP_CODE_INVALID,
                "Ma nhom nhan khong hop le.",
                new { groupCode = code });
        return code;
    }

    private static string NormalizeName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw LabelValidation(AppErrorCode.LABEL_NAME_REQUIRED, "Ten nhan khong duoc trong.");
        if (name.Length > 120)
            throw LabelValidation(
                AppErrorCode.LABEL_NAME_TOO_LONG,
                "Ten nhan toi da 120 ky tu.",
                new { length = name.Length, maxLength = 120 });
        return name;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length > 500 ? text[..500] : text;
    }

    private static string? NormalizeColor(string? value)
    {
        var color = value?.Trim();
        if (string.IsNullOrWhiteSpace(color)) return null;
        if (!HexColorRegex.IsMatch(color))
            throw LabelValidation(
                AppErrorCode.LABEL_COLOR_INVALID,
                "Mau nhan phai la ma hex dang #RRGGBB.",
                new { color });
        return color.ToUpperInvariant();
    }

    private static string NormalizeValueSourceType(string? value, string? usage, string? dataType)
    {
        var sourceType = LabelValueSourceTypes.Normalize(value);
        if (sourceType == LabelValueSourceTypes.None)
            return sourceType;

        var normalizedUsage = NormalizeUsage(usage);
        var normalizedType = LabelDataTypes.Normalize(dataType);
        var supportsCodeSource = normalizedType is LabelDataTypes.ShortText or LabelDataTypes.StringList;
        if (!supportsCodeSource)
        {
            throw LabelValidation(
                AppErrorCode.LABEL_USAGE_INVALID,
                "Nguon gia tri nhan chi ap dung cho SHORT_TEXT hoac STRING_LIST.",
                new { sourceType, dataType = normalizedType });
        }

        if (normalizedUsage != LabelUsages.TableTarget && normalizedUsage != LabelUsages.Statistic)
        {
            throw LabelValidation(
                AppErrorCode.LABEL_USAGE_INVALID,
                "Nguon gia tri nhan chi ap dung cho nhan thong ke hoac nhan vi tri bang.",
                new { sourceType, usage = normalizedUsage });
        }

        return sourceType;
    }

    private static List<LabelValueOption> NormalizeValueOptions(
        IReadOnlyList<LabelValueOptionDto>? value,
        string? sourceType)
    {
        var normalizedSource = LabelValueSourceTypes.Normalize(sourceType);
        if (normalizedSource != LabelValueSourceTypes.FixedEnum)
            return new List<LabelValueOption>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<LabelValueOption>();
        foreach (var option in value ?? Array.Empty<LabelValueOptionDto>())
        {
            var code = option.Code?.Trim();
            var label = option.Label?.Trim();
            if (string.IsNullOrWhiteSpace(code))
                continue;
            if (!CodeRegex.IsMatch(code.ToLowerInvariant()))
                throw LabelValidation(
                    AppErrorCode.LABEL_CODE_INVALID,
                    "Ma gia tri enum nhan khong hop le.",
                    new { code });
            if (!seen.Add(code))
                throw LabelValidation(
                    AppErrorCode.LABEL_CODE_INVALID,
                    "Ma gia tri enum nhan bi trung.",
                    new { code });

            rows.Add(new LabelValueOption
            {
                Code = code,
                Label = string.IsNullOrWhiteSpace(label) ? code : label
            });
        }

        if (rows.Count == 0)
            throw LabelValidation(
                AppErrorCode.LABEL_CODE_INVALID,
                "Nguon FIXED_ENUM can it nhat mot gia tri.",
                new { sourceType = normalizedSource });
        if (rows.Count > 100)
            throw LabelValidation(
                AppErrorCode.LABEL_CODE_INVALID,
                "Nguon FIXED_ENUM chi ho tro toi da 100 gia tri.",
                new { count = rows.Count, max = 100 });

        return rows;
    }

    private static FilterDefinition<LabelCatalogItem> BuildUsageSearchFilter(string requestedUsage)
    {
        var fb = Builders<LabelCatalogItem>.Filter;
        return fb.Eq(x => x.Usage, requestedUsage);
    }

    private static string NormalizeUsage(string? value, string fallback = LabelUsages.Classification, bool allowGenericFallback = true)
    {
        var usage = LabelUsages.Normalize(value, allowGenericFallback ? fallback : string.Empty);
        if (usage == string.Empty)
            throw LabelValidation(
                AppErrorCode.LABEL_USAGE_INVALID,
                "Muc dich su dung nhan khong hop le.",
                new { usage = value });
        return usage;
    }

    private static string NormalizeScopeType(string? value)
    {
        var scope = value?.Trim().ToUpperInvariant();
        return scope switch
        {
            LabelScopeTypes.Global => LabelScopeTypes.Global,
            LabelScopeTypes.Level => LabelScopeTypes.Level,
            LabelScopeTypes.Unit => LabelScopeTypes.Unit,
            _ => throw LabelValidation(
                AppErrorCode.LABEL_SCOPE_TYPE_INVALID,
                "ScopeType nhan khong hop le.",
                new { scopeType = value })
        };
    }

    private static string NormalizeScopeId(string? scopeId, string scopeType)
    {
        var id = scopeId?.Trim();
        if (scopeType == LabelScopeTypes.Global)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            throw LabelValidation(
                AppErrorCode.LABEL_SCOPE_ID_REQUIRED,
                "ScopeId la bat buoc voi nhan LEVEL hoac UNIT.",
                new { scopeType });
        if (!ObjectId.TryParse(id, out _))
            throw LabelValidation(
                AppErrorCode.LABEL_SCOPE_ID_INVALID,
                "ScopeId nhan khong hop le.",
                new { scopeType, scopeId = id });
        return id;
    }
}
