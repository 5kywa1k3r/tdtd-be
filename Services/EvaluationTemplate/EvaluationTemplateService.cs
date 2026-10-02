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
        var scopes = VisibleScopes();
        var rows = await _ctx.EvaluationTemplates
            .Find(x => !x.IsDeleted && x.IsActive && scopes.Contains(x.UnitCodeScope))
            .SortBy(x => x.RepresentativeLabel)
            .ThenBy(x => x.RepresentativeCode)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<EvaluationTemplateDto>> GetAllAsync(CancellationToken ct)
    {
        var scopes = VisibleScopes();
        var rows = await _ctx.EvaluationTemplates
            .Find(x => !x.IsDeleted && scopes.Contains(x.UnitCodeScope))
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

        var scopes = VisibleScopes();
        var doc = await _ctx.EvaluationTemplates
            .Find(x => x.Id == id && !x.IsDeleted && scopes.Contains(x.UnitCodeScope))
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
        var unitCodeScope = OwnScope(me);
        if (!string.IsNullOrWhiteSpace(req.UnitCodeScope) && req.UnitCodeScope.Trim() != unitCodeScope)
            throw AppExceptionFactory.Forbidden(AppErrorCode.EVALUATION_TEMPLATE_MANAGE_FORBIDDEN);
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

    public async Task<EvaluationTemplateDto> UpdateAsync(string id, UpdateEvaluationTemplateRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        EnsureCanManage(me);
        if (string.IsNullOrWhiteSpace(id))
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_ID_REQUIRED, new { id });

        var ownScope = OwnScope(me);
        var doc = await _ctx.EvaluationTemplates
            .Find(x => x.Id == id && !x.IsDeleted && x.CreatedByUserId == me.Id && x.UnitCodeScope == ownScope)
            .FirstOrDefaultAsync(ct)
            ?? throw EvaluationTemplateNotFound(id);

        if (!string.IsNullOrWhiteSpace(req.UnitCodeScope) && req.UnitCodeScope.Trim() != ownScope)
            throw AppExceptionFactory.Forbidden(AppErrorCode.EVALUATION_TEMPLATE_MANAGE_FORBIDDEN);
        if (req.IsActive != doc.IsActive)
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_UPDATE_UNSUPPORTED, new { reason = "USE_DEACTIVATE_ACTION" });

        // Changing codes/labels after a Work has used them would rewrite the
        // meaning of historical evaluations. Deactivation remains available.
        var usedByWork = await _ctx.Works.Find(x => x.EvaluationTemplateId == id && !x.IsDeleted).AnyAsync(ct);
        var usedByAssignment = await _ctx.WorkAssignments.Find(x => x.EvaluationTemplateId == id && !x.IsDeleted).AnyAsync(ct);
        if (usedByWork || usedByAssignment)
            throw AppExceptionFactory.Create(AppErrorCode.EVALUATION_TEMPLATE_IN_USE, new { id });

        var label = NormalizeLabel(req.RepresentativeLabel, "representativeLabel");
        var items = NormalizeItems(req.Items);
        var now = DateTime.UtcNow;
        var result = await _ctx.EvaluationTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted && x.CreatedByUserId == me.Id
                && x.UnitCodeScope == ownScope && x.UpdatedAtUtc == doc.UpdatedAtUtc,
            Builders<EvaluationTemplate>.Update
                .Set(x => x.RepresentativeLabel, label)
                .Set(x => x.Items, items)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);
        if (result.MatchedCount == 0)
            throw AppExceptionFactory.Create(AppErrorCode.EVALUATION_TEMPLATE_UPDATE_CONFLICT, new { id });

        doc.RepresentativeLabel = label;
        doc.Items = items;
        doc.UpdatedAtUtc = now;
        doc.UpdatedByUserId = me.Id;
        return ToDto(doc);
    }

    public async Task DeactivateAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        EnsureCanManage(me);

        if (string.IsNullOrWhiteSpace(id))
            throw EvaluationTemplateBadRequest(AppErrorCode.EVALUATION_TEMPLATE_ID_REQUIRED, new { id });

        var ownScope = OwnScope(me);
        var rs = await _ctx.EvaluationTemplates.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted && x.UnitCodeScope == ownScope && x.CreatedByUserId == me.Id,
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

    private IReadOnlyList<string> VisibleScopes()
    {
        var scopes = EvaluationTemplatePermissionPolicy.VisibleUnitCodes(_me.RequireMe().UnitCode);
        if (scopes.Count == 0)
            throw AppExceptionFactory.Forbidden(AppErrorCode.EVALUATION_TEMPLATE_MANAGE_FORBIDDEN);
        return scopes;
    }

    private static string OwnScope(MeResponse me)
        => EvaluationTemplatePermissionPolicy.VisibleUnitCodes(me.UnitCode).FirstOrDefault()
           ?? throw AppExceptionFactory.Forbidden(AppErrorCode.EVALUATION_TEMPLATE_MANAGE_FORBIDDEN);

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

    private EvaluationTemplateDto ToDto(EvaluationTemplate x)
        => new(
            x.Id,
            x.RepresentativeCode,
            x.RepresentativeLabel,
            x.Items.Count,
            x.IsActive,
            x.UnitCodeScope,
            x.Items.OrderBy(i => i.Order).ThenBy(i => i.Code)
                .Select(i => new EvaluationTemplateItemDto(i.Code, i.Label, i.Order, i.IsActive))
                .ToList(),
            x.CreatedByUserId == _me.RequireMe().Id && x.UnitCodeScope == OwnScope(_me.RequireMe()));

    private static void EnsureCanManage(MeResponse me)
    {
        var unitCode = (me.UnitCode ?? string.Empty).Trim();
        var roles = me.Roles ?? new List<string>();

        var hasAllowedRole = roles.Any(role =>
            !string.IsNullOrWhiteSpace(role) &&
            EvaluationTemplatePermissionPolicy.AllowedRolePrefixes.Any(prefix =>
                (string.Equals(role, prefix, StringComparison.OrdinalIgnoreCase)
                 || role.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)))
        );
        var hasManagerAccount = me.AccountKind is "SYSTEM_ADMIN" or "LEVEL_MANAGER" or "UNIT_MANAGER";
        if ((!hasAllowedRole && !hasManagerAccount) || EvaluationTemplatePermissionPolicy.VisibleUnitCodes(unitCode).Count == 0)
            throw AppExceptionFactory.Forbidden(AppErrorCode.EVALUATION_TEMPLATE_MANAGE_FORBIDDEN, new
            {
                me.Id,
                unitCode,
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
