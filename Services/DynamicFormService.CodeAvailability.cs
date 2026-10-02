using MongoDB.Driver;
using tdtd_be.DTOs.DynamicForms;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    public async Task<DynamicFormCodeAvailability> GetCodeAvailabilityAsync(string? code, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var normalized = NormalizeCode(code);
        if (normalized is null) return new(true, null, false); // The server assigns blank codes on create.
        // Match the unique (code, versionNo) index for a new root (version 1).
        // Published status, unit and owner do not change code uniqueness.
        var existing = await _ctx.DynamicFormTemplates
            .Find(x => !x.IsDeleted && x.Code == normalized && x.VersionNo == 1)
            .Project(x => new { x.Id, x.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (existing is null) return new(true, null, false);
        var owned = existing.CreatedByUserId == me.Id;
        return new(false, owned ? existing.Id : null, !owned);
    }
}
