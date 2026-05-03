using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.Models;

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

    public LabelService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public async Task<PagedResult<LabelRow>> SearchAsync(LabelSearchReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        req ??= new LabelSearchReq(null, null, null, null, null, null, null);

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
            throw new ArgumentNullException(nameof(req));

        var scope = ResolveManagedScope(me, req.ScopeType, req.ScopeId);
        var name = NormalizeName(req.Name);
        var now = DateTime.UtcNow;
        var doc = new LabelCatalogItem
        {
            Code = NormalizeCode(req.Code),
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Description = NormalizeOptionalText(req.Description),
            Color = NormalizeColor(req.Color),
            GroupCode = NormalizeOptionalCode(req.GroupCode),
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
            throw new InvalidOperationException("Mã nhãn đã tồn tại trong phạm vi này.");
        }

        return ToRow(doc, me);
    }

    public async Task<LabelRow> UpdateAsync(string id, UpdateLabelReq req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireLabelManager(me);
        if (req is null)
            throw new ArgumentNullException(nameof(req));

        var doc = await LoadVisibleAsync(id, me, ct);
        EnsureCanManage(me, doc);
        if (doc.IsSystem)
            throw new InvalidOperationException("Không được sửa nhãn hệ thống.");

        var name = NormalizeName(req.Name);
        var now = DateTime.UtcNow;
        var update = Builders<LabelCatalogItem>.Update
            .Set(x => x.Name, name)
            .Set(x => x.NameLower, name.ToLowerInvariant())
            .Set(x => x.Description, NormalizeOptionalText(req.Description))
            .Set(x => x.Color, NormalizeColor(req.Color))
            .Set(x => x.GroupCode, NormalizeOptionalCode(req.GroupCode))
            .Set(x => x.IsActive, req.IsActive)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        await _ctx.Labels.UpdateOneAsync(x => x.Id == id && !x.IsDeleted, update, cancellationToken: ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RequireLabelManager(me);

        var doc = await LoadVisibleAsync(id, me, ct);
        EnsureCanManage(me, doc);
        if (doc.IsSystem)
            throw new InvalidOperationException("Không được xóa nhãn hệ thống.");

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
    }

    private async Task<LabelCatalogItem> LoadVisibleAsync(string id, MeResponse me, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id không được trống.", nameof(id));

        var filter = Builders<LabelCatalogItem>.Filter.Eq(x => x.Id, id.Trim())
                     & Builders<LabelCatalogItem>.Filter.Eq(x => x.IsDeleted, false)
                     & BuildVisibilityFilter(me);

        var doc = await _ctx.Labels.Find(filter).FirstOrDefaultAsync(ct);
        if (doc is null)
            throw new InvalidOperationException("Không tìm thấy nhãn.");

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
            throw new BadHttpRequestException("SYSTEM_ADMIN, MANAGER_LEVEL hoặc MANAGER_UNIT required.");
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

        throw new BadHttpRequestException("Bạn không có quyền quản lý nhãn này.");
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
                throw new BadHttpRequestException("Không xác định được phạm vi level của tài khoản.");

            EnsureRequestedScopeMatches(requestedScopeType, requestedScopeId, LabelScopeTypes.Level, me.UnitId);
            return (LabelScopeTypes.Level, me.UnitId);
        }

        throw new BadHttpRequestException("Không có quyền quản lý nhãn.");
    }

    private static void EnsureRequestedScopeMatches(
        string? requestedScopeType,
        string? requestedScopeId,
        string expectedScopeType,
        string expectedScopeId)
    {
        if (!string.IsNullOrWhiteSpace(requestedScopeType) &&
            NormalizeScopeType(requestedScopeType) != expectedScopeType)
            throw new BadHttpRequestException("Phạm vi nhãn không khớp quyền quản lý.");

        if (!string.IsNullOrWhiteSpace(requestedScopeId) &&
            !string.Equals(requestedScopeId.Trim(), expectedScopeId, StringComparison.Ordinal))
            throw new BadHttpRequestException("Phạm vi nhãn không khớp quyền quản lý.");
    }

    private static LabelRow ToRow(LabelCatalogItem x, MeResponse me)
        => new(
            x.Id,
            x.Code,
            x.Name,
            x.Description,
            x.Color,
            x.GroupCode,
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
            "createdatutc" => desc ? sort.Descending(x => x.CreatedAtUtc) : sort.Ascending(x => x.CreatedAtUtc),
            "updatedatutc" => desc ? sort.Descending(x => x.UpdatedAtUtc) : sort.Ascending(x => x.UpdatedAtUtc),
            _ => desc ? sort.Descending(x => x.NameLower) : sort.Ascending(x => x.NameLower)
        };
    }

    private static string NormalizeCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Mã nhãn không được trống.");
        if (!CodeRegex.IsMatch(code))
            throw new ArgumentException("Mã nhãn chỉ gồm chữ thường, số, dấu -, _ hoặc . và tối đa 64 ký tự.");
        return code;
    }

    private static string? NormalizeOptionalCode(string? value)
    {
        var code = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (!CodeRegex.IsMatch(code))
            throw new ArgumentException("Mã nhóm nhãn không hợp lệ.");
        return code;
    }

    private static string NormalizeName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tên nhãn không được trống.");
        if (name.Length > 120)
            throw new ArgumentException("Tên nhãn tối đa 120 ký tự.");
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
            throw new ArgumentException("Màu nhãn phải là mã hex dạng #RRGGBB.");
        return color.ToUpperInvariant();
    }

    private static string NormalizeScopeType(string? value)
    {
        var scope = value?.Trim().ToUpperInvariant();
        return scope switch
        {
            LabelScopeTypes.Global => LabelScopeTypes.Global,
            LabelScopeTypes.Level => LabelScopeTypes.Level,
            LabelScopeTypes.Unit => LabelScopeTypes.Unit,
            _ => throw new ArgumentException("ScopeType nhãn không hợp lệ.")
        };
    }

    private static string NormalizeScopeId(string? scopeId, string scopeType)
    {
        var id = scopeId?.Trim();
        if (scopeType == LabelScopeTypes.Global)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("ScopeId là bắt buộc với nhãn LEVEL hoặc UNIT.");
        if (!ObjectId.TryParse(id, out _))
            throw new ArgumentException("ScopeId nhãn không hợp lệ.");
        return id;
    }
}
