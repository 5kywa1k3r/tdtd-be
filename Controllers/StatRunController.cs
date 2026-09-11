using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/stat-runs")]
public sealed class StatRunController : ControllerBase
{
    private readonly IStatRunService _service;
    private readonly IStatRunCandidateActivation _activation;
    private readonly IHostEnvironment _environment;
    private readonly MeAccessor _me;

    public StatRunController(
        IStatRunService service,
        IStatRunCandidateActivation activation,
        IHostEnvironment environment,
        MeAccessor me)
    {
        _service = service;
        _activation = activation;
        _environment = environment;
        _me = me;
    }

    [HttpPost("{capabilityId}/jobs")]
    public async Task<ActionResult<StatRunJobResponse>> Create(
        string capabilityId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunCreateRequest>(body);
        var result = await _service.CreateAsync(capabilityId, request, actor, ct);
        return StatusCode(
            result.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status202Accepted,
            result.Job);
    }

    [HttpGet("jobs/{jobId}")]
    public Task<StatRunJobResponse> Get(string jobId, CancellationToken ct)
        => _service.GetAsync(jobId, _me.RequireMe(), ct);

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/activation/evaluate")]
    public ActionResult<StatRunCandidateEvaluation> EvaluateActivation([FromBody] JsonElement body)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        if (!_environment.IsEnvironment("Testing"))
            return NotFound();
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunActivationEvaluateRequest>(body);
        return Ok(_activation.EvaluateFoundation(request.CapabilityId, request.RouteId));
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/jobs/claim")]
    public async Task<ActionResult<StatRunWorkerLeaseResponse>> Claim(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunWorkerClaimRequest>(body);
        var result = await _service.ClaimAsync(request.WorkerId, actor, ct);
        return result is null ? NoContent() : Ok(result);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/jobs/{jobId}/heartbeat")]
    public Task<StatRunJobResponse> Heartbeat(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunWorkerFenceRequest>(body);
        return _service.HeartbeatAsync(jobId, request, actor, ct);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/jobs/{jobId}/complete")]
    public Task<StatRunJobResponse> Complete(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunWorkerFenceRequest>(body);
        return _service.CompleteFoundationAsync(jobId, request, actor, ct);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/jobs/{jobId}/retry")]
    public Task<StatRunJobResponse> Retry(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunWorkerRetryRequest>(body);
        return _service.RetryAsync(jobId, request, actor, ct);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/jobs/{jobId}/expire-lease")]
    public Task<StatRunJobResponse> ExpireLease(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunWorkerFenceRequest>(body);
        return _service.ExpireLeaseAsync(jobId, request, actor, ct);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p9/stat-runs/jobs/{jobId}/reset")]
    public Task<StatRunJobResponse> Reset(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunResetRequest>(body);
        return _service.ResetAsync(jobId, request, actor, ct);
    }
}
