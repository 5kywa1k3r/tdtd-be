using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/label-enum-catalogs")]
[Authorize]
public sealed class LabelEnumCatalogsController : ControllerBase
{
    private readonly ILabelEnumCatalogService _svc;

    public LabelEnumCatalogsController(ILabelEnumCatalogService svc)
    {
        _svc = svc;
    }

    [HttpPost("search")]
    public Task<PagedResult<LabelEnumCatalogRow>> Search([FromBody] LabelEnumCatalogSearchReq req, CancellationToken ct)
        => _svc.SearchAsync(req, ct);

    [HttpGet("{id}")]
    public Task<LabelEnumCatalogDetail> GetById([FromRoute] string id, CancellationToken ct)
        => _svc.GetByIdAsync(id, ct);

    [HttpPost]
    public Task<LabelEnumCatalogDetail> Create([FromBody] CreateLabelEnumCatalogReq req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPost("quick-create")]
    public Task<LabelEnumCatalogDetail> QuickCreate([FromBody] QuickCreateLabelEnumCatalogReq req, CancellationToken ct)
        => _svc.QuickCreateAsync(req, ct);

    [HttpPut("{id}")]
    public Task<LabelEnumCatalogDetail> Update(
        [FromRoute] string id,
        [FromBody] UpdateLabelEnumCatalogReq req,
        CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpDelete("{id}")]
    public Task Delete([FromRoute] string id, CancellationToken ct)
        => _svc.DeleteAsync(id, ct);

    [HttpGet("{id}/options/search")]
    public Task<PagedResult<LabelEnumOptionPickRow>> SearchOptions(
        [FromRoute] string id,
        [FromQuery] string? q,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
        => _svc.SearchOptionsAsync(id, q, page, pageSize, ct);
}
