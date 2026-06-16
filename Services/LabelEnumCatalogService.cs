using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface ILabelEnumCatalogService
{
    Task<PagedResult<LabelEnumCatalogRow>> SearchAsync(LabelEnumCatalogSearchReq req, CancellationToken ct);
    Task<LabelEnumCatalogDetail> GetByIdAsync(string id, CancellationToken ct);
    Task<LabelEnumCatalogDetail> CreateAsync(CreateLabelEnumCatalogReq req, CancellationToken ct);
    Task<LabelEnumCatalogDetail> QuickCreateAsync(QuickCreateLabelEnumCatalogReq req, CancellationToken ct);
    Task<LabelEnumCatalogDetail> UpdateAsync(string id, UpdateLabelEnumCatalogReq req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<PagedResult<LabelEnumOptionPickRow>> SearchOptionsAsync(string catalogId, string? q, int page, int pageSize, CancellationToken ct);
    Task<LabelEnumCatalog> EnsureVisibleActiveCatalogAsync(string catalogId, CancellationToken ct);
    Task ValidateVisibleActiveCatalogsAsync(IEnumerable<string?> catalogIds, CancellationToken ct);
    Task<IReadOnlyDictionary<string, RuntimeEnumOptionSet>> LoadActiveOptionSetsAsync(IEnumerable<string?> catalogIds, CancellationToken ct);
}

public sealed record RuntimeEnumOptionSet(
    string CatalogId,
    IReadOnlySet<string> Codes);

public sealed class LabelEnumCatalogService : ILabelEnumCatalogService
{
    private static readonly Regex CodeRegex = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public LabelEnumCatalogService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public async Task<PagedResult<LabelEnumCatalogRow>> SearchAsync(LabelEnumCatalogSearchReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        req ??= new LabelEnumCatalogSearchReq(null, null, null, null, null, null);
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var fb = Builders<LabelEnumCatalog>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false) & await BuildVisibleFilterAsync(me, ct);
        if (!IsEnumManager(me))
            filter &= fb.Eq(x => x.IsActive, true);
        else if (req.IsActive.HasValue)
            filter &= fb.Eq(x => x.IsActive, req.IsActive.Value);

        if (!string.IsNullOrWhiteSpace(req.Code))
            filter &= fb.Regex(x => x.Code, new BsonRegularExpression(Regex.Escape(req.Code.Trim()), "i"));
        if (!string.IsNullOrWhiteSpace(req.Name))
            filter &= fb.Regex(x => x.NameLower, new BsonRegularExpression(Regex.Escape(req.Name.Trim().ToLowerInvariant()), "i"));
        if (!string.IsNullOrWhiteSpace(req.ScopeType))
            filter &= fb.Eq(x => x.ScopeType, NormalizeScopeType(req.ScopeType));
        if (!string.IsNullOrWhiteSpace(req.ScopeId))
            filter &= fb.Eq(x => x.ScopeId, req.ScopeId.Trim());
        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var rx = new BsonRegularExpression(Regex.Escape(req.Q.Trim()), "i");
            filter &= fb.Or(fb.Regex(x => x.Code, rx), fb.Regex(x => x.Name, rx), fb.Regex(x => x.NameLower, rx));
        }

        var total = await _ctx.LabelEnumCatalogs.CountDocumentsAsync(filter, cancellationToken: ct);
        var docs = await _ctx.LabelEnumCatalogs
            .Find(filter)
            .Sort(BuildSort(req.SortField, req.SortDirection))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<LabelEnumCatalogRow>(
            docs.Select(x => ToRow(x, me)).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<LabelEnumCatalogDetail> GetByIdAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadVisibleAsync(id, me, ct);
        return ToDetail(doc, me);
    }

    public Task<LabelEnumCatalogDetail> CreateAsync(CreateLabelEnumCatalogReq req, CancellationToken ct)
        => CreateCoreAsync(
            req?.Code,
            req?.Name,
            req?.Description,
            req?.Options,
            req?.ScopeType,
            req?.ScopeId,
            req?.IsActive ?? true,
            sourceFeature: "LABEL_ADMIN",
            sourcePath: "label-enum-catalogs",
            ct);

    public Task<LabelEnumCatalogDetail> QuickCreateAsync(QuickCreateLabelEnumCatalogReq req, CancellationToken ct)
        => CreateCoreAsync(
            req?.Code,
            req?.Name,
            req?.Description,
            req?.Options,
            req?.ScopeType,
            req?.ScopeId,
            isActive: true,
            sourceFeature: NormalizeOptionalText(req?.SourceFeature) ?? "QUICK_CREATE",
            sourcePath: NormalizeOptionalText(req?.SourcePath) ?? "dynamic-template",
            ct);

    public async Task<LabelEnumCatalogDetail> UpdateAsync(string id, UpdateLabelEnumCatalogReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireEnumManager(me);
        if (req is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_REQUEST_REQUIRED);

        var doc = await LoadVisibleAsync(id, me, ct);
        EnsureCanManage(me, doc);

        var options = NormalizeOptions(req.Options);
        var name = NormalizeName(req.Name);
        var now = DateTime.UtcNow;
        var nextRevision = Math.Max(1, doc.OptionsRevision + 1);
        var update = Builders<LabelEnumCatalog>.Update
            .Set(x => x.Name, name)
            .Set(x => x.NameLower, name.ToLowerInvariant())
            .Set(x => x.Description, NormalizeOptionalText(req.Description))
            .Set(x => x.Options, options)
            .Set(x => x.OptionsRevision, nextRevision)
            .Set(x => x.IsActive, req.IsActive)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        await _ctx.LabelEnumCatalogs.UpdateOneAsync(x => x.Id == id && !x.IsDeleted, update, cancellationToken: ct);

        doc.Name = name;
        doc.NameLower = name.ToLowerInvariant();
        doc.Description = NormalizeOptionalText(req.Description);
        doc.Options = options;
        doc.OptionsRevision = nextRevision;
        doc.IsActive = req.IsActive;
        doc.UpdatedAtUtc = now;
        doc.UpdatedByUserId = me.Id;
        await RebuildOptionReadModelAsync(doc, ct);

        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireEnumManager(me);
        var doc = await LoadVisibleAsync(id, me, ct);
        EnsureCanManage(me, doc);

        var usedByLabel = await _ctx.Labels
            .Find(x => x.ValueSourceCatalogId == id && !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
        if (usedByLabel)
            throw AppExceptionFactory.Create(AppErrorCode.LABEL_ENUM_CATALOG_IN_USE, new { catalogId = id });

        var catalogRefRegex = BuildCatalogReferenceRegex(id);
        var usedByDynamicExcel = await _ctx.DynamicExcelTemplates
            .Find(Builders<DynamicExcelTemplate>.Filter.Eq(x => x.IsDeleted, false)
                  & Builders<DynamicExcelTemplate>.Filter.Regex(x => x.SpecJson, catalogRefRegex))
            .Limit(1)
            .AnyAsync(ct);
        if (usedByDynamicExcel)
            throw AppExceptionFactory.Create(AppErrorCode.LABEL_ENUM_CATALOG_IN_USE, new { catalogId = id, source = "DYNAMIC_EXCEL" });

        var formFb = Builders<DynamicFormTemplate>.Filter;
        var usedByDynamicForm = await _ctx.DynamicFormTemplates
            .Find(formFb.Eq(x => x.IsDeleted, false)
                  & formFb.Or(
                      formFb.Regex(x => x.FieldsJson, catalogRefRegex),
                      formFb.Regex(x => x.ExcelBlockJson, catalogRefRegex),
                      formFb.Regex(x => x.BlocksJson, catalogRefRegex)))
            .Limit(1)
            .AnyAsync(ct);
        if (usedByDynamicForm)
            throw AppExceptionFactory.Create(AppErrorCode.LABEL_ENUM_CATALOG_IN_USE, new { catalogId = id, source = "DYNAMIC_FORM" });

        var now = DateTime.UtcNow;
        await _ctx.LabelEnumCatalogs.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<LabelEnumCatalog>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.IsActive, false)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, me.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        await _ctx.LabelEnumOptionReadModels.UpdateManyAsync(
            x => x.CatalogId == id && !x.IsDeleted,
            Builders<LabelEnumOptionReadModel>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.IsActive, false)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, me.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);
    }

    private static BsonRegularExpression BuildCatalogReferenceRegex(string catalogId)
    {
        var escaped = Regex.Escape(catalogId.Trim());
        return new BsonRegularExpression($"\\\"(?:catalogId|valueSourceCatalogId)\\\"\\s*:\\s*\\\"{escaped}\\\"", "i");
    }

    public async Task<PagedResult<LabelEnumOptionPickRow>> SearchOptionsAsync(
        string catalogId,
        string? q,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        _ = await EnsureVisibleActiveCatalogAsync(catalogId, ct);
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var fb = Builders<LabelEnumOptionReadModel>.Filter;
        var filter = fb.Eq(x => x.CatalogId, catalogId.Trim())
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsActive, true);

        if (!string.IsNullOrWhiteSpace(q))
            filter &= fb.Regex(x => x.SearchText, new BsonRegularExpression(Regex.Escape(NormalizeSearchText(q)), "i"));

        var total = await _ctx.LabelEnumOptionReadModels.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.LabelEnumOptionReadModels
            .Find(filter)
            .SortBy(x => x.Order).ThenBy(x => x.Code)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new LabelEnumOptionPickRow(x.Id, x.CatalogId, x.CatalogCode, x.Code, x.Label, x.Order))
            .ToListAsync(ct);

        return new PagedResult<LabelEnumOptionPickRow>(rows, total, page, pageSize);
    }

    public async Task<LabelEnumCatalog> EnsureVisibleActiveCatalogAsync(string catalogId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var doc = await LoadVisibleAsync(catalogId, me, ct);
        if (!doc.IsActive)
            throw AppExceptionFactory.NotFound(AppErrorCode.LABEL_ENUM_CATALOG_NOT_FOUND, new { catalogId });
        return doc;
    }

    public async Task ValidateVisibleActiveCatalogsAsync(IEnumerable<string?> catalogIds, CancellationToken ct)
    {
        var ids = NormalizeIds(catalogIds);
        foreach (var id in ids)
            _ = await EnsureVisibleActiveCatalogAsync(id, ct);
    }

    public async Task<IReadOnlyDictionary<string, RuntimeEnumOptionSet>> LoadActiveOptionSetsAsync(
        IEnumerable<string?> catalogIds,
        CancellationToken ct)
    {
        var ids = NormalizeIds(catalogIds);
        if (ids.Count == 0)
            return new Dictionary<string, RuntimeEnumOptionSet>(StringComparer.Ordinal);

        var rows = await _ctx.LabelEnumOptionReadModels
            .Find(x => ids.Contains(x.CatalogId) && x.IsActive && !x.IsDeleted)
            .Project(x => new { x.CatalogId, x.Code })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.CatalogId, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => new RuntimeEnumOptionSet(
                    x.Key,
                    x.Select(row => row.Code).Where(code => !string.IsNullOrWhiteSpace(code)).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                StringComparer.Ordinal);
    }

    private async Task<LabelEnumCatalogDetail> CreateCoreAsync(
        string? requestedCode,
        string? requestedName,
        string? description,
        IReadOnlyList<LabelEnumOptionDto>? requestedOptions,
        string? requestedScopeType,
        string? requestedScopeId,
        bool isActive,
        string sourceFeature,
        string sourcePath,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireEnumManager(me);

        var scope = await ResolveManagedScopeAsync(me, requestedScopeType, requestedScopeId, ct);
        var name = NormalizeName(requestedName);
        var code = await NormalizeOrCreateCodeAsync(requestedCode, name, scope.ScopeType, scope.ScopeId, ct);
        var options = NormalizeOptions(requestedOptions);
        var now = DateTime.UtcNow;
        var doc = new LabelEnumCatalog
        {
            Code = code,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Description = NormalizeOptionalText(description),
            ScopeType = scope.ScopeType,
            ScopeId = scope.ScopeId,
            ScopeUnitCode = scope.ScopeUnitCode,
            ScopeLevel = scope.ScopeLevel,
            CreatedByUsername = me.Username,
            CreatedByAccountKind = me.AccountKind,
            Options = options,
            OptionsRevision = 1,
            IsActive = isActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            IsDeleted = false
        };

        try
        {
            await _ctx.LabelEnumCatalogs.InsertOneAsync(doc, cancellationToken: ct);
            await RebuildOptionReadModelAsync(doc, ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.LABEL_ENUM_CATALOG_DUPLICATE_CODE,
                new { doc.Code, doc.ScopeType, doc.ScopeId, sourceFeature, sourcePath });
        }

        return ToDetail(doc, me);
    }

    private async Task RebuildOptionReadModelAsync(LabelEnumCatalog doc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.LabelEnumOptionReadModels.UpdateManyAsync(
            x => x.CatalogId == doc.Id && !x.IsDeleted,
            Builders<LabelEnumOptionReadModel>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);

        var rows = doc.Options
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code)
            .Select(option => new LabelEnumOptionReadModel
            {
                Id = ObjectId.GenerateNewId().ToString(),
                CatalogId = doc.Id,
                CatalogCode = doc.Code,
                CatalogName = doc.Name,
                ScopeType = doc.ScopeType,
                ScopeId = doc.ScopeId,
                ScopeUnitCode = doc.ScopeUnitCode,
                ScopeLevel = doc.ScopeLevel,
                Code = option.Code,
                Label = option.Label,
                LabelLower = option.Label.ToLowerInvariant(),
                SearchText = NormalizeSearchText($"{option.Code} {option.Label}"),
                Order = option.Order,
                IsActive = option.IsActive && doc.IsActive,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = doc.UpdatedByUserId ?? doc.CreatedByUserId,
                UpdatedByUserId = doc.UpdatedByUserId ?? doc.CreatedByUserId,
                IsDeleted = false
            })
            .ToList();

        if (rows.Count > 0)
            await _ctx.LabelEnumOptionReadModels.InsertManyAsync(rows, cancellationToken: ct);
    }

    private async Task<LabelEnumCatalog> LoadVisibleAsync(string id, MeResponse me, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_ID_REQUIRED, new { id });

        var filter = Builders<LabelEnumCatalog>.Filter.Eq(x => x.Id, id.Trim())
                     & Builders<LabelEnumCatalog>.Filter.Eq(x => x.IsDeleted, false)
                     & await BuildVisibleFilterAsync(me, ct);

        var doc = await _ctx.LabelEnumCatalogs.Find(filter).FirstOrDefaultAsync(ct);
        if (doc is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.LABEL_ENUM_CATALOG_NOT_FOUND, new { id });
        return doc;
    }

    private async Task<FilterDefinition<LabelEnumCatalog>> BuildVisibleFilterAsync(MeResponse me, CancellationToken ct)
    {
        var fb = Builders<LabelEnumCatalog>.Filter;
        if (RoleGuard.IsSystemAdmin(me))
            return FilterDefinition<LabelEnumCatalog>.Empty;

        var filters = new List<FilterDefinition<LabelEnumCatalog>>
        {
            fb.Eq(x => x.ScopeType, LabelScopeTypes.Global)
        };

        var ancestorIds = await LoadAncestorUnitIdsAsync(me.UnitId, ct);
        if (ancestorIds.Count > 0)
        {
            filters.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Unit) & fb.In(x => x.ScopeId, ancestorIds));
            filters.Add(fb.Eq(x => x.ScopeType, LabelScopeTypes.Level) & fb.In(x => x.ScopeId, ancestorIds));
        }

        return fb.Or(filters);
    }

    private async Task<ManagedScope> ResolveManagedScopeAsync(
        MeResponse me,
        string? requestedScopeType,
        string? requestedScopeId,
        CancellationToken ct)
    {
        if (RoleGuard.IsSystemAdmin(me))
        {
            var type = string.IsNullOrWhiteSpace(requestedScopeType)
                ? LabelScopeTypes.Global
                : NormalizeScopeType(requestedScopeType);
            var id = type == LabelScopeTypes.Global ? null : NormalizeScopeId(requestedScopeId, type);
            var unit = id is null ? null : await LoadUnitRequiredAsync(id, ct);
            return new ManagedScope(type, id, unit?.Code, unit?.Level);
        }

        if (RoleGuard.TryGetManagerUnit(me, out var unitId))
        {
            EnsureRequestedScopeMatches(requestedScopeType, requestedScopeId, LabelScopeTypes.Unit, unitId);
            var unit = await LoadUnitRequiredAsync(unitId, ct);
            return new ManagedScope(LabelScopeTypes.Unit, unitId, unit.Code, unit.Level);
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            if (string.IsNullOrWhiteSpace(me.UnitId))
                throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_SCOPE_LEVEL_UNAVAILABLE, new { me.Id, me.Username });
            EnsureRequestedScopeMatches(requestedScopeType, requestedScopeId, LabelScopeTypes.Level, me.UnitId);
            var unit = await LoadUnitRequiredAsync(me.UnitId, ct);
            return new ManagedScope(LabelScopeTypes.Level, me.UnitId, unit.Code, unit.Level);
        }

        throw AppExceptionFactory.Forbidden(AppErrorCode.LABEL_ENUM_CATALOG_MANAGER_REQUIRED, new { me.Id, me.Roles });
    }

    private async Task<Unit> LoadUnitRequiredAsync(string unitId, CancellationToken ct)
    {
        var unit = await _ctx.Units.Find(x => x.Id == unitId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (unit is null)
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_SCOPE_ID_INVALID, new { unitId });
        return unit;
    }

    private async Task<List<string>> LoadAncestorUnitIdsAsync(string? unitId, CancellationToken ct)
    {
        var result = new List<string>();
        var currentId = unitId?.Trim();
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(currentId) && guard < 32)
        {
            var unit = await _ctx.Units
                .Find(x => x.Id == currentId && !x.IsDeleted)
                .Project(x => new { x.Id, x.ParentUnitId })
                .FirstOrDefaultAsync(ct);
            if (unit is null)
                break;
            result.Add(unit.Id);
            currentId = unit.ParentUnitId;
            guard++;
        }

        return result;
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
                AppErrorCode.LABEL_ENUM_CATALOG_SCOPE_MISMATCH,
                new { requestedScopeType, requestedScopeId, expectedScopeType, expectedScopeId });

        if (!string.IsNullOrWhiteSpace(requestedScopeId) &&
            !string.Equals(requestedScopeId.Trim(), expectedScopeId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.LABEL_ENUM_CATALOG_SCOPE_MISMATCH,
                new { requestedScopeType, requestedScopeId, expectedScopeType, expectedScopeId });
    }

    private static void EnsureCanManage(MeResponse me, LabelEnumCatalog doc)
    {
        if (RoleGuard.IsSystemAdmin(me))
            return;
        if (RoleGuard.TryGetManagerUnit(me, out var unitId) &&
            doc.ScopeType == LabelScopeTypes.Unit &&
            doc.ScopeId == unitId)
            return;
        if (RoleGuard.IsManagerLevel(me) &&
            doc.ScopeType == LabelScopeTypes.Level &&
            doc.ScopeId == me.UnitId)
            return;

        throw AppExceptionFactory.Forbidden(
            AppErrorCode.LABEL_ENUM_CATALOG_MANAGE_FORBIDDEN,
            new { catalogId = doc.Id, doc.Code, doc.ScopeType, doc.ScopeId, actorUserId = me.Id });
    }

    private static bool IsEnumManager(MeResponse me)
        => RoleGuard.IsSystemAdmin(me) || RoleGuard.IsManagerLevel(me) || RoleGuard.TryGetManagerUnit(me, out _);

    private static void RequireEnumManager(MeResponse me)
    {
        if (!IsEnumManager(me))
            throw AppExceptionFactory.Forbidden(AppErrorCode.LABEL_ENUM_CATALOG_MANAGER_REQUIRED, new { me.Id, me.Roles });
    }

    private static List<LabelEnumOption> NormalizeOptions(IReadOnlyList<LabelEnumOptionDto>? input)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LabelEnumOption>();
        var index = 0;
        foreach (var item in input ?? Array.Empty<LabelEnumOptionDto>())
        {
            var code = item.Code?.Trim();
            var label = item.Label?.Trim();
            if (string.IsNullOrWhiteSpace(code))
                continue;
            var normalizedCode = code.ToLowerInvariant();
            if (!CodeRegex.IsMatch(normalizedCode))
                throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_OPTION_INVALID, new { code });
            if (!seen.Add(normalizedCode))
                throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_OPTION_DUPLICATE, new { code = normalizedCode });
            if (label?.Length > 200)
                throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_OPTION_INVALID, new { code = normalizedCode, maxLabelLength = 200 });

            result.Add(new LabelEnumOption
            {
                Code = normalizedCode,
                Label = string.IsNullOrWhiteSpace(label) ? normalizedCode : label,
                Order = item.Order > 0 ? item.Order : index,
                IsActive = item.IsActive
            });
            index++;
        }

        if (result.Count(x => x.IsActive) == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_OPTION_REQUIRED);
        if (result.Count > 1000)
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_OPTION_INVALID, new { count = result.Count, max = 1000 });
        return result.OrderBy(x => x.Order).ThenBy(x => x.Code).ToList();
    }

    private async Task<string> NormalizeOrCreateCodeAsync(string? requestedCode, string name, string scopeType, string? scopeId, CancellationToken ct)
    {
        var code = requestedCode?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (!CodeRegex.IsMatch(code))
                throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_CODE_INVALID, new { code });
            return code;
        }

        var baseCode = Slugify(name);
        if (string.IsNullOrWhiteSpace(baseCode))
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_CODE_REQUIRED);

        var fb = Builders<LabelEnumCatalog>.Filter;
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var candidate = suffix == 0 ? baseCode : $"{baseCode}_{suffix + 1}";
            var exists = await _ctx.LabelEnumCatalogs
                .Find(fb.Eq(x => x.ScopeType, scopeType) &
                      fb.Eq(x => x.ScopeId, scopeId) &
                      fb.Eq(x => x.Code, candidate) &
                      fb.Eq(x => x.IsDeleted, false))
                .Limit(1)
                .AnyAsync(ct);
            if (!exists)
                return candidate;
        }

        return $"{baseCode}_{ObjectId.GenerateNewId().ToString()[..6]}";
    }

    private static string Slugify(string value)
    {
        var formD = value.Trim().Normalize(NormalizationForm.FormD);
        var chars = formD
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        var noMarks = new string(chars).Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var slug = Regex.Replace(noMarks, "[^a-z0-9_.-]+", "_").Trim('_', '.', '-');
        if (string.IsNullOrWhiteSpace(slug))
            return string.Empty;
        if (!char.IsLetterOrDigit(slug[0]))
            slug = $"enum_{slug}";
        return slug.Length <= 64 ? slug : slug[..64].TrimEnd('_', '.', '-');
    }

    private static string NormalizeName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_NAME_REQUIRED);
        if (name.Length > 160)
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_ENUM_CATALOG_NAME_REQUIRED, new { maxLength = 160 });
        return name;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Length > 500 ? text[..500] : text;
    }

    private static string NormalizeScopeType(string? value)
    {
        var type = value?.Trim().ToUpperInvariant();
        return type switch
        {
            LabelScopeTypes.Global => LabelScopeTypes.Global,
            LabelScopeTypes.Unit => LabelScopeTypes.Unit,
            LabelScopeTypes.Level => LabelScopeTypes.Level,
            _ => throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_SCOPE_TYPE_INVALID, new { scopeType = value })
        };
    }

    private static string NormalizeScopeId(string? scopeId, string scopeType)
    {
        if (scopeType == LabelScopeTypes.Global)
            return string.Empty;
        var id = scopeId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_SCOPE_ID_REQUIRED, new { scopeType });
        if (!ObjectId.TryParse(id, out _))
            throw AppExceptionFactory.BadRequest(AppErrorCode.LABEL_SCOPE_ID_INVALID, new { scopeType, scopeId = id });
        return id;
    }

    private static string NormalizeSearchText(string value)
    {
        var text = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = text
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .ToArray();
        return Regex.Replace(new string(chars).Normalize(NormalizationForm.FormC), "\\s+", " ");
    }

    private static IReadOnlyList<string> NormalizeIds(IEnumerable<string?> ids)
        => ids
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

    private static SortDefinition<LabelEnumCatalog> BuildSort(string? field, string? direction)
    {
        var desc = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        var sort = Builders<LabelEnumCatalog>.Sort;
        return (field ?? "name").ToLowerInvariant() switch
        {
            "code" => desc ? sort.Descending(x => x.Code) : sort.Ascending(x => x.Code),
            "createdatutc" => desc ? sort.Descending(x => x.CreatedAtUtc) : sort.Ascending(x => x.CreatedAtUtc),
            "updatedatutc" => desc ? sort.Descending(x => x.UpdatedAtUtc) : sort.Ascending(x => x.UpdatedAtUtc),
            _ => desc ? sort.Descending(x => x.NameLower) : sort.Ascending(x => x.NameLower)
        };
    }

    private static LabelEnumCatalogRow ToRow(LabelEnumCatalog x, MeResponse me)
        => new(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.ScopeType,
            x.ScopeId,
            x.ScopeUnitCode,
            x.ScopeLevel,
            x.Options.Count(option => option.IsActive),
            x.Options.Count,
            x.IsActive,
            IsEnumManager(me) && CanManage(me, x),
            x.CreatedByUsername,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);

    private static LabelEnumCatalogDetail ToDetail(LabelEnumCatalog x, MeResponse me)
        => new(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.ScopeType,
            x.ScopeId,
            x.ScopeUnitCode,
            x.ScopeLevel,
            x.Options
                .OrderBy(option => option.Order)
                .ThenBy(option => option.Code)
                .Select(option => new LabelEnumOptionDto(option.Code, option.Label, option.Order, option.IsActive))
                .ToList(),
            x.IsActive,
            IsEnumManager(me) && CanManage(me, x),
            x.CreatedByUsername,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);

    private static bool CanManage(MeResponse me, LabelEnumCatalog x)
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

    private sealed record ManagedScope(
        string ScopeType,
        string? ScopeId,
        string? ScopeUnitCode,
        int? ScopeLevel);
}
