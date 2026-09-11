using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/operations/job-runs/stat-config-readiness-jobs")]
public sealed class StatConfigReadinessAdminController : ControllerBase
{
    private readonly IStatConfigOperationsService _operations;
    private readonly MeAccessor _me;

    public StatConfigReadinessAdminController(
        IStatConfigOperationsService operations,
        MeAccessor me)
    {
        _operations = operations;
        _me = me;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? status = null,
        [FromQuery] string? ownerKind = null,
        [FromQuery] string? ownerId = null,
        [FromQuery] string? configId = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var actor = RequireSystemAdmin();
        return Ok(await _operations.SearchDiagnosticsAsync(
            new StatConfigValidationJobSearchRequest
            {
                Status = status,
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                ConfigId = configId,
                CorrelationId = correlationId,
                Query = q,
                IncludeInactive = includeInactive,
                Page = page,
                PageSize = pageSize
            },
            actor,
            ct));
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> GetDiagnostics(
        [FromRoute] string jobId,
        CancellationToken ct)
    {
        var actor = RequireSystemAdmin();
        return Ok(await _operations.GetDiagnosticsAsync(jobId, actor, ct));
    }

    [HttpPost("process")]
    public async Task<IActionResult> Process(
        [FromQuery] int maxJobs = 10,
        CancellationToken ct = default)
    {
        var actor = RequireSystemAdmin();
        return Ok(await _operations.ProcessPendingAsync(maxJobs, actor, ct));
    }

    [HttpPost("{jobId}/reset")]
    public async Task<IActionResult> Reset(
        [FromRoute] string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireSystemAdmin();
        var command = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<StatConfigValidationResetPayload>>(body);
        return Ok(await _operations.ResetAsync(jobId, command, actor, ct));
    }

    [HttpPost("{jobId}/cancel")]
    public async Task<IActionResult> Cancel(
        [FromRoute] string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireSystemAdmin();
        var command = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<StatConfigValidationCancelPayload>>(body);
        return Ok(await _operations.CancelAsync(jobId, command, actor, ct));
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireSystemAdmin();
        var command = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<StatConfigValidationCleanupPayload>>(body);
        return Ok(await _operations.CleanupAsync(command, actor, ct));
    }

    [HttpGet("indexes")]
    public async Task<IActionResult> ValidateIndexes(CancellationToken ct)
    {
        var actor = RequireSystemAdmin();
        return Ok(await _operations.ValidateIndexesAsync(actor, ct));
    }

    private tdtd_be.DTOs.Auth.MeResponse RequireSystemAdmin()
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        return actor;
    }
}
