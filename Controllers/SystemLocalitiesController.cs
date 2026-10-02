using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

/// <summary>Public to authenticated users. No write route: the official baseline is immutable.</summary>
[ApiController]
[Authorize]
[Route("api/pickers/localities")]
public sealed class SystemLocalitiesController(MeAccessor me) : ControllerBase
{
    [HttpGet("search")]
    public ActionResult<PagedResult<SystemLocalityCatalog.Locality>> Search(
        [FromQuery] string? q = null, [FromQuery] int page = 0, [FromQuery] int pageSize = 50)
    {
        _ = me.RequireMe();
        page = Math.Clamp(page, 0, 10000);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = SystemLocalityCatalog.SearchKey(q);
        var rows = SystemLocalityCatalog.Official.Rows.Where(x => query.Length == 0 ||
            x.Code.Contains(query, StringComparison.Ordinal) || SystemLocalityCatalog.SearchKey(x.Name).Contains(query, StringComparison.Ordinal))
            .OrderByDescending(x => x.Code == query).ThenBy(x => x.Code).ToList();
        return Ok(new PagedResult<SystemLocalityCatalog.Locality>(rows.Skip(page * pageSize).Take(pageSize).ToList(), rows.Count, page, pageSize));
    }

    [HttpGet("source")]
    public IActionResult Source()
    {
        _ = me.RequireMe();
        var data = SystemLocalityCatalog.Official;
        return Ok(new { data.DatasetId, data.ProvinceCode, data.ProvinceName, data.EffectiveFrom,
            data.VerifiedOn, data.SourceDocument, data.SourceUrl, data.SourceSha256, count = data.Rows.Count, readOnly = true });
    }
}
