using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/works/{workId}/statistics/{scopeAssignmentId}/reconciliations")]
public sealed class StatisticReconciliationController : ControllerBase
{
    private readonly IStatisticReconciliationRunService _service;
    private readonly IStatisticReconciliationCandidateActivation _activation;
    private readonly IHostEnvironment _environment;
    private readonly MeAccessor _me;

    public StatisticReconciliationController(
        IStatisticReconciliationRunService service,
        IStatisticReconciliationCandidateActivation activation,
        IHostEnvironment environment,
        MeAccessor me)
    {
        _service = service;
        _activation = activation;
        _environment = environment;
        _me = me;
    }


    [HttpPost("capture-plan-preflight")]
    public Task<StatisticReconciliationCapturePlanPreflightResponse>
        PreflightCapturePlan(
            string workId,
            string scopeAssignmentId,
            [FromBody] JsonElement body,
            CancellationToken ct)
        => _service.PreflightCapturePlanAsync(
            workId,
            scopeAssignmentId,
            body,
            _me.RequireMe(),
            ct);

    [HttpPost]
    public async Task<ActionResult<StatisticReconciliationSummaryResponse>> Create(
        string workId,
        string scopeAssignmentId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        var result = await _service.CreateAsync(
            workId,
            scopeAssignmentId,
            body,
            actor,
            ct);
        return StatusCode(
            result.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status202Accepted,
            result.Run);
    }

    [HttpGet]
    public Task<StatisticReconciliationPageResponse> List(
        string workId,
        string scopeAssignmentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
        => _service.ListAsync(
            workId,
            scopeAssignmentId,
            page,
            pageSize,
            _me.RequireMe(),
            ct);

    [HttpGet("{reconciliationId}")]
    public Task<StatisticReconciliationSummaryResponse> GetSummary(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
        => _service.GetSummaryAsync(
            workId,
            scopeAssignmentId,
            reconciliationId,
            _me.RequireMe(),
            ct);

    [HttpGet("{reconciliationId}/detail")]
    public Task<StatisticReconciliationDetailResponse> GetDetail(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
        => _service.GetDetailAsync(
            workId,
            scopeAssignmentId,
            reconciliationId,
            _me.RequireMe(),
            ct);

    [HttpPost("{reconciliationId}/cancel")]
    public Task<StatisticReconciliationSummaryResponse> Cancel(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        return _service.CancelAsync(
            workId,
            scopeAssignmentId,
            reconciliationId,
            body,
            actor,
            ct);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p10/statistic-reconciliations/activation/evaluate")]
    public ActionResult<StatisticReconciliationCandidateEvaluation> EvaluateActivation(
        [FromBody] JsonElement body)
    {
        RoleGuard.RequireSystemAdmin(_me.RequireMe());
        if (!_environment.IsEnvironment("Testing"))
            return NotFound();
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationActivationEvaluateRequest>(body);
        return Ok(_activation.EvaluateFoundation(
            request.CapabilityId,
            request.RouteId));
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p10/statistic-reconciliations/jobs/claim")]
    public async Task<ActionResult<StatisticReconciliationWorkerLeaseResponse>> Claim(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireTestingSystemAdmin();
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationWorkerClaimRequest>(body);
        var result = await _service.ClaimAsync(request, actor, ct);
        return result is null ? NoContent() : Ok(result);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p10/statistic-reconciliations/jobs/{reconciliationId}/heartbeat")]
    public async Task<ActionResult<StatisticReconciliationDetailResponse>> Heartbeat(
        string reconciliationId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireTestingSystemAdmin();
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationWorkerFenceRequest>(body);
        return Ok(await _service.HeartbeatAsync(reconciliationId, request, actor, ct));
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p10/statistic-reconciliations/jobs/{reconciliationId}/fail")]
    public async Task<ActionResult<StatisticReconciliationDetailResponse>> Fail(
        string reconciliationId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireTestingSystemAdmin();
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationWorkerFailRequest>(body);
        return Ok(await _service.FailAsync(reconciliationId, request, actor, ct));
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("~/api/testing/p10/statistic-reconciliations/jobs/{reconciliationId}/publish-pending")]
    public async Task<ActionResult<StatisticReconciliationDetailResponse>> PublishPending(
        string reconciliationId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = RequireTestingSystemAdmin();
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationPendingPublishRequest>(body);
        return Ok(await _service.PublishPendingAsync(reconciliationId, request, actor, ct));
    }

    private tdtd_be.DTOs.Auth.MeResponse RequireTestingSystemAdmin()
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        if (!_environment.IsEnvironment("Testing"))
            throw new tdtd_be.Common.Errors.AppException(
                tdtd_be.Common.Errors.AppErrorCode.STAT_RECONCILIATION_NOT_FOUND,
                new { writes = 0 });
        return actor;
    }
}
