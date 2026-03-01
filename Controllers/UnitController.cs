using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Units;
using tdtd_be.Services;
using static tdtd_be.Services.UnitService;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/admin/units")]
[Authorize]
public sealed class UnitsController : ControllerBase
{
    private readonly IUnitService _svc;
    public UnitsController(IUnitService svc) => _svc = svc;

    [HttpPost] public Task<UnitResponse> Create([FromBody] CreateUnitRequest req, CancellationToken ct) => _svc.CreateAsync(req, ct);
    [HttpPut("{unitId}")] public Task<UnitResponse> Update(string unitId, [FromBody] UpdateUnitRequest req, CancellationToken ct) => _svc.UpdateAsync(unitId, req, ct);

    [HttpPatch("{unitId}/soft-delete")]
    public async Task<IActionResult> SoftDelete(string unitId, CancellationToken ct)
    {
        await _svc.DeleteAsync(unitId, ct);
        return NoContent();
    }

    [HttpGet("roots")] public Task<IReadOnlyList<UnitResponse>> Roots(CancellationToken ct) => _svc.ListRootsAsync(ct);
    [HttpGet("{parentUnitId}/children")] public Task<IReadOnlyList<UnitResponse>> Children(string parentUnitId, CancellationToken ct) => _svc.ListChildrenAsync(parentUnitId, ct);

    // Subtree by prefix code
    [HttpGet("search-by-code-prefix")]
    public Task<IReadOnlyList<UnitResponse>> SearchByCodePrefix([FromQuery] string prefix, CancellationToken ct)
        => _svc.SearchByCodePrefixAsync(prefix, ct);

    [HttpGet("{unitId}/history")]
    public Task<IReadOnlyList<UnitHistoryResponse>> History(string unitId, [FromQuery] int take = 50, CancellationToken ct = default)
        => _svc.GetHistoryAsync(unitId, take, ct);

    [HttpGet("children")]
    public Task<IReadOnlyList<UnitPickNodeDTO>> GetChildren([FromQuery] string? parentId, CancellationToken ct) => _svc.GetChildrenAsync(parentId, ct);
}