using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/stat-config")]
public sealed class StatConfigBundleController : ControllerBase
{
    private readonly IStatConfigBundleService _bundles;
    private readonly MeAccessor _me;

    public StatConfigBundleController(
        IStatConfigBundleService bundles,
        MeAccessor me)
    {
        _bundles = bundles;
        _me = me;
    }

    [HttpGet("bundle")]
    public IActionResult ReadEmpty(
        [FromQuery] string ownerKind,
        [FromQuery] string ownerId)
    {
        RoleGuard.RequireSystemAdmin(_me.RequireMe());
        return Ok(_bundles.ReadEmpty(ownerKind, ownerId));
    }

    [HttpPost("bundle/readback")]
    public async Task<IActionResult> Read(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        RoleGuard.RequireSystemAdmin(_me.RequireMe());
        var request = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigBundleReadRequest>(body);
        return Ok(await _bundles.ReadAsync(request, ct));
    }

    [HttpPost("bundle/validate")]
    public async Task<IActionResult> Validate(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        RoleGuard.RequireSystemAdmin(_me.RequireMe());
        var request = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigBundleValidateRequest>(body);
        return Ok(await _bundles.ValidateAsync(request, ct));
    }

    [HttpPost("barriers/{entry}")]
    public IActionResult Barrier(
        [FromRoute] string entry,
        [FromBody] JsonElement body)
    {
        RoleGuard.RequireSystemAdmin(_me.RequireMe());
        var request = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigPhaseBarrierRequest>(body);
        StatConfigPhaseBarrier.ValidateProbeRequest(request);
        StatConfigPhaseBarrier.Reject(entry);
        return NoContent();
    }
}
