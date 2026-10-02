using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;

namespace tdtd_be.Services;

public sealed partial class DynamicFormService
{
    // This picker means ownership, not administrative authority, read or clone grants.
    // Apply the same actor-derived predicate before search/count/paging AND detail materialization.
    internal static FilterDefinition<DynamicFormTemplate> BuildSectionSourceOwnerFilter(string userId)
    {
        var f = Builders<DynamicFormTemplate>.Filter;
        return f.Eq(x => x.IsDeleted, false) & f.Eq(x => x.CreatedByUserId, userId);
    }

    public async Task<PagedResult<DynamicFormSectionSourceRow>> SearchSectionSourcesAsync(
        DynamicFormSectionSourceQuery req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var page = Math.Max(0, req.Page);
        var size = Math.Clamp(req.PageSize, 1, 20);
        var offset = (long)page * size;
        if (offset > MaxSearchOffset)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "DYNAMIC_FORM_SEARCH_OFFSET_TOO_LARGE" });
        var f = Builders<DynamicFormTemplate>.Filter;
        var filter = BuildSectionSourceOwnerFilter(me.Id);
        // A new unsaved Canvas form has a local ID, not a Mongo ObjectId.
        if (ObjectId.TryParse(req.ExcludeFormId, out var excludedId))
            filter &= f.Ne(x => x.Id, excludedId.ToString());
        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var rx = BuildLiteralSearchRegex(req.Q, nameof(req.Q));
            filter &= f.Regex("code", rx) | f.Regex("name", rx);
        }
        var total = await _ctx.DynamicFormTemplates.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.DynamicFormTemplates.Find(filter)
            .SortBy(x => x.Code).ThenByDescending(x => x.VersionNo).ThenBy(x => x.Id)
            .Skip((int)offset).Limit(size)
            .Project(x => new DynamicFormSectionSourceRow(x.Id, x.Code, x.Name, x.VersionNo,
                x.Revision, x.IsPublished, x.CreatedByUsername)).ToListAsync(ct);
        return new PagedResult<DynamicFormSectionSourceRow>(rows, total, page, size);
    }

    public async Task<DynamicFormDetail> GetSectionSourceAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        if (!ObjectId.TryParse(id, out var sourceId))
            throw AppExceptionFactory.Forbidden(AppErrorCode.DYNAMIC_FORM_READ_FORBIDDEN,
                new { reason = "DYNAMIC_FORM_SECTION_SOURCE_UNAVAILABLE" });
        var filter = BuildSectionSourceOwnerFilter(me.Id) & Builders<DynamicFormTemplate>.Filter.Eq(x => x.Id, sourceId.ToString());
        var doc = await _ctx.DynamicFormTemplates.Find(filter).FirstOrDefaultAsync(ct);
        // Hidden, removed and missing IDs have the same response, with no source metadata.
        if (doc is null)
            throw AppExceptionFactory.Forbidden(AppErrorCode.DYNAMIC_FORM_READ_FORBIDDEN,
                new { reason = "DYNAMIC_FORM_SECTION_SOURCE_UNAVAILABLE" });
        return await ToDetailAsync(doc, me, ct);
    }
}
