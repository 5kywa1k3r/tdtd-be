using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.EvaluationTemplates;
using tdtd_be.Models;

namespace tdtd_be.Services.EvaluationTemplates;

public interface IEvaluationTemplateService
{
    Task<IReadOnlyList<EvaluationTemplateDto>> GetActiveAsync(CancellationToken ct);
    Task<IReadOnlyList<EvaluationTemplateDto>> GetAllAsync(CancellationToken ct);
    Task<EvaluationTemplateDto> GetByIdAsync(string id, CancellationToken ct);
    Task<EvaluationTemplateDto> CreateAsync(CreateEvaluationTemplateRequest req, CancellationToken ct);
    Task<EvaluationTemplateDto> UpdateAsync(string id, UpdateEvaluationTemplateRequest req, CancellationToken ct);
    Task DeactivateAsync(string id, CancellationToken ct);
}

public sealed class EvaluationTemplateService : IEvaluationTemplateService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public EvaluationTemplateService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public async Task<IReadOnlyList<EvaluationTemplateDto>> GetActiveAsync(CancellationToken ct)
    {
        var rows = await _ctx.EvaluationTemplates
            .Find(x => !x.IsDeleted && x.IsActive)
            .SortBy(x => x.RepresentativeLabel)
            .ThenBy(x => x.RepresentativeCode)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<EvaluationTemplateDto>> GetAllAsync(CancellationToken ct)
    {
        var rows = await _ctx.EvaluationTemplates
            .Find(x => !x.IsDeleted)
            .SortByDescending(x => x.IsActive)
            .ThenBy(x => x.RepresentativeLabel)
            .ThenBy(x => x.RepresentativeCode)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<EvaluationTemplateDto> GetByIdAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Thiếu id bộ mã đánh giá.");

        var doc = await _ctx.EvaluationTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy bộ mã đánh giá.");

        return ToDto(doc);
    }

    public async Task<EvaluationTemplateDto> CreateAsync(CreateEvaluationTemplateRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        EnsureCanManage(me);

        var now = DateTime.UtcNow;
        var representativeCode = NormalizeCode(req.RepresentativeCode);
        var representativeLabel = NormalizeLabel(req.RepresentativeLabel, "Phải nhập tên bộ mã.");
        var unitCodeScope = NormalizeScope(req.UnitCodeScope, me);
        var items = NormalizeItems(req.Items);

        var duplicateCode = await _ctx.EvaluationTemplates
            .Find(x => !x.IsDeleted && x.RepresentativeCode == representativeCode)
            .AnyAsync(ct);
        if (duplicateCode)
            throw new InvalidOperationException("Mã đại diện đã tồn tại.");

        var doc = new EvaluationTemplate
        {
            RepresentativeCode = representativeCode,
            RepresentativeLabel = representativeLabel,
            UnitCodeScope = unitCodeScope,
            Items = items,
            IsActive = true,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
        };

        await _ctx.EvaluationTemplates.InsertOneAsync(doc, cancellationToken: ct);
        return ToDto(doc);
    }

    public Task<EvaluationTemplateDto> UpdateAsync(string id, UpdateEvaluationTemplateRequest req, CancellationToken ct)
        => throw new InvalidOperationException("Không hỗ trợ cập nhật bộ mã đánh giá. Hãy ngừng dùng bộ cũ và tạo bộ mới.");

    public async Task DeactivateAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        EnsureCanManage(me);

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Thiếu id bộ mã đánh giá.");

        var rs = await _ctx.EvaluationTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<EvaluationTemplate>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw new InvalidOperationException("Không tìm thấy bộ mã đánh giá.");
    }

    private static string NormalizeCode(string value)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Phải nhập mã đại diện.");
        return code;
    }

    private static string NormalizeLabel(string? value, string message)
    {
        var label = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label))
            throw new InvalidOperationException(message);
        return label;
    }

    private static string NormalizeScope(string? requestedScope, MeResponse me)
    {
        var scope = (requestedScope ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(scope))
            return scope.ToUpperInvariant();

        var fromUnitSymbol = (me.UnitSymbol ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromUnitSymbol))
            return fromUnitSymbol.ToUpperInvariant();

        return EvaluationTemplatePermissionPolicy.AllowedUnitCode;
    }

    private static List<EvaluationTemplateItem> NormalizeItems(IEnumerable<CreateEvaluationTemplateItemRequest> items)
        => NormalizeItems(items.Select((x, i) => new UpdateEvaluationTemplateItemRequest(x.Code, x.Label, x.Order ?? i + 1, x.IsActive ?? true)));

    private static List<EvaluationTemplateItem> NormalizeItems(IEnumerable<UpdateEvaluationTemplateItemRequest> items)
    {
        var normalized = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.Label))
            .Select((x, i) => new EvaluationTemplateItem
            {
                Code = NormalizeCode(x.Code),
                Label = NormalizeLabel(x.Label, "Mã con phải có nhãn."),
                Order = x.Order ?? i + 1,
                IsActive = x.IsActive ?? true,
            })
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code)
            .ToList();

        if (normalized.Count == 0)
            throw new InvalidOperationException("Phải có ít nhất 1 mã con.");

        var duplicate = normalized
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Mã con bị trùng: {duplicate.Key}");

        return normalized;
    }

    private static EvaluationTemplateDto ToDto(EvaluationTemplate x)
        => new(
            x.Id,
            x.RepresentativeCode,
            x.RepresentativeLabel,
            x.Items.Count,
            x.IsActive,
            x.UnitCodeScope,
            x.Items.OrderBy(i => i.Order).ThenBy(i => i.Code)
                .Select(i => new EvaluationTemplateItemDto(i.Code, i.Label, i.Order, i.IsActive))
                .ToList());

    private static void EnsureCanManage(MeResponse me)
    {
        var unitCode = (me.UnitCode ?? string.Empty).Trim();
        var unitSymbol = (me.UnitSymbol ?? string.Empty).Trim();
        var positionCode = (me.PositionCode ?? string.Empty).Trim();
        var roles = me.Roles ?? new List<string>();

        var hasAllowedRole = roles.Any(role =>
            !string.IsNullOrWhiteSpace(role) &&
            EvaluationTemplatePermissionPolicy.AllowedRolePrefixes.Any(prefix =>
                role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        );

        var isPv01 = string.Equals(me.UnitCode, EvaluationTemplatePermissionPolicy.AllowedUnitCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(me.UnitSymbol, EvaluationTemplatePermissionPolicy.AllowedUnitCode, StringComparison.OrdinalIgnoreCase);

        var hasAllowedPosition =
            !string.IsNullOrWhiteSpace(me.PositionCode) &&
            EvaluationTemplatePermissionPolicy.AllowedPositionCodes.Contains(me.PositionCode, StringComparer.OrdinalIgnoreCase);

        if (!(isPv01 && hasAllowedPosition) && !hasAllowedRole)
            throw new UnauthorizedAccessException("Bạn không có quyền quản lý bộ mã đánh giá.");
    }
}
