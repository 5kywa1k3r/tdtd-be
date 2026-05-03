using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/labels")]
[Authorize]
public sealed class LabelsController : ControllerBase
{
    private readonly ILabelService _svc;

    public LabelsController(ILabelService svc)
    {
        _svc = svc;
    }

    [HttpPost("search")]
    public Task<PagedResult<LabelRow>> Search([FromBody] LabelSearchReq req, CancellationToken ct)
        => _svc.SearchAsync(req, ct);

    [HttpGet("{id}")]
    public Task<LabelRow> GetById([FromRoute] string id, CancellationToken ct)
        => _svc.GetByIdAsync(id, ct);

    [HttpPost]
    public Task<LabelRow> Create([FromBody] CreateLabelReq req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPut("{id}")]
    public Task<LabelRow> Update(
        [FromRoute] string id,
        [FromBody] UpdateLabelReq req,
        CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpDelete("{id}")]
    public Task Delete([FromRoute] string id, CancellationToken ct)
        => _svc.DeleteAsync(id, ct);
}
