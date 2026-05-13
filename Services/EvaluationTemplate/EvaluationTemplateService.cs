using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
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
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_ID_REQUIRED, new { id });

        var doc = await _ctx.EvaluationTemplates
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw EvaluationTemplateNotFound(id);

        return ToDto(doc);
    }

    public async Task<EvaluationTemplateDto> CreateAsync(CreateEvaluationTemplateRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        EnsureCanManage(me);

        var now = DateTime.UtcNow;
        var representativeCode = NormalizeCode(req.RepresentativeCode);
        var representativeLabel = NormalizeLabel(req.RepresentativeLabel, "representativeLabel");
        var unitCodeScope = NormalizeScope(req.UnitCodeScope, me);
        var items = NormalizeItems(req.Items);

        var duplicateCode = await _ctx.EvaluationTemplates
            .Find(x => !x.IsDeleted && x.RepresentativeCode == representativeCode)
            .AnyAsync(ct);
        if (duplicateCode)
            throw EvaluationTemplateConflict(new { field = "representativeCode", value = representativeCode });

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
        => throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_UPDATE_UNSUPPORTED, new { id });

    public async Task DeactivateAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        EnsureCanManage(me);

        if (string.IsNullOrWhiteSpace(id))
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_ID_REQUIRED, new { id });

        var rs = await _ctx.EvaluationTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            Builders<EvaluationTemplate>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw EvaluationTemplateNotFound(id);
    }

    private static string NormalizeCode(string value)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_CODE_REQUIRED, new { field = "code" });
        return code;
    }

    private static string NormalizeLabel(string? value, string field)
    {
        var label = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label))
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_LABEL_REQUIRED, new { field });
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
                Label = NormalizeLabel(x.Label, "items.label"),
                Order = x.Order ?? i + 1,
                IsActive = x.IsActive ?? true,
            })
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code)
            .ToList();

        if (normalized.Count == 0)
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_ITEM_REQUIRED, new { field = "items" });

        var duplicate = normalized
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw AppExceptionFactory.Create(AppErrorCode.EVALUATION_TEMPLATE_ITEM_DUPLICATE, new { code = duplicate.Key });

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

        var isPv01 = string.Equals(unitCode, EvaluationTemplatePermissionPolicy.AllowedUnitCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(unitSymbol, EvaluationTemplatePermissionPolicy.AllowedUnitCode, StringComparison.OrdinalIgnoreCase);

        var hasAllowedPosition =
            !string.IsNullOrWhiteSpace(positionCode) &&
            EvaluationTemplatePermissionPolicy.AllowedPositionCodes.Contains(positionCode, StringComparer.OrdinalIgnoreCase);

        if (!(isPv01 && hasAllowedPosition) && !hasAllowedRole)
            throw AppExceptionFactory.Forbidden(AppErrorCode.EVALUATION_TEMPLATE_MANAGE_FORBIDDEN, new
            {
                me.Id,
                unitCode,
                unitSymbol,
                positionCode,
                roles
            });
    }

    private static AppException EvaluationTemplateBadRequest(AppErrorCode code, object? details = null)
        => AppExceptionFactory.BadRequest(code, details);

    private static AppException EvaluationTemplateNotFound(string? id)
        => AppExceptionFactory.NotFound(AppErrorCode.EVALUATION_TEMPLATE_NOT_FOUND, new { id });

    private static AppException EvaluationTemplateConflict(object? details)
        => AppExceptionFactory.Create(AppErrorCode.EVALUATION_TEMPLATE_CONFLICT, details);
}
