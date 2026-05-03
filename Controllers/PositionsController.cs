using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Positions;
using tdtd_be.Services;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/admin/positions")]
[Authorize]
public sealed class PositionsController : ControllerBase
{
    private readonly IPositionAdminService _svc;

    public PositionsController(IPositionAdminService svc)
    {
        _svc = svc;
    }

    [HttpPost]
    public Task<PositionResponse> Create([FromBody] CreatePositionRequest req, CancellationToken ct)
        => _svc.CreateAsync(req, ct);

    [HttpPut("{id}")]
    public Task<PositionResponse> Update(string id, [FromBody] UpdatePositionRequest req, CancellationToken ct)
        => _svc.UpdateAsync(id, req, ct);

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _svc.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet]
    public Task<IReadOnlyList<PositionResponse>> List(
        [FromQuery] bool? isDeleted,
        [FromQuery] string? unitTypeCode,
        CancellationToken ct)
        => _svc.ListAsync(isDeleted, unitTypeCode, ct);

    [HttpGet("by-unit-type/{unitTypeCode}")]
    public Task<IReadOnlyList<PositionResponse>> ByUnitType(string unitTypeCode, CancellationToken ct)
        => _svc.ListAsync(false, unitTypeCode, ct);
}
