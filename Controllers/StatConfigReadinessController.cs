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
public sealed class StatConfigReadinessController : ControllerBase
{
    private readonly IStatConfigOperationsService _operations;
    private readonly MeAccessor _me;

    public StatConfigReadinessController(
        IStatConfigOperationsService operations,
        MeAccessor me)
    {
        _operations = operations;
        _me = me;
    }

    [HttpPost("owners/{ownerKind}/{ownerId}/readiness-jobs")]
    public async Task<IActionResult> Enqueue(
        [FromRoute] string ownerKind,
        [FromRoute] string ownerId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var command = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<StatConfigValidationEnqueuePayload>>(
                body);
        return Ok(await _operations.EnqueueAsync(
            ownerKind,
            ownerId,
            command,
            _me.RequireMe(),
            ct));
    }

    [HttpGet("readiness/jobs/{jobId}")]
    public async Task<IActionResult> GetSafeStatus(
        [FromRoute] string jobId,
        CancellationToken ct)
        => Ok(await _operations.GetSafeStatusAsync(
            jobId,
            _me.RequireMe(),
            ct));
}
