using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/operations/jobs")]
public sealed class StatRunOperationsController : ControllerBase
{
    private readonly IStatRunService _service;
    private readonly MeAccessor _me;

    public StatRunOperationsController(IStatRunService service, MeAccessor me)
    {
        _service = service;
        _me = me;
    }

    [HttpGet]
    public Task<StatRunOperationsJobPageResponse> List(
        [FromQuery] int limit = 50,
        [FromQuery] string? status = null,
        [FromQuery] string? runKind = null,
        [FromQuery] string? workId = null,
        CancellationToken ct = default)
        => _service.ListOperationsAsync(
            limit,
            status,
            runKind,
            workId,
            _me.RequireMe(),
            ct);

    [HttpGet("{jobId}")]
    public Task<StatRunOperationsJobResponse> Get(
        string jobId,
        CancellationToken ct)
        => _service.GetOperationsAsync(jobId, _me.RequireMe(), ct);

    [HttpGet("{jobId}/diagnostics")]
    public Task<StatRunJobDiagnosticResponse> Diagnostics(
        string jobId,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        return _service.GetOperationDiagnosticsAsync(jobId, actor, ct);
    }

    [HttpPost("{jobId}/retry")]
    public Task<StatRunOperationMutationResponse> Retry(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Mutate(jobId, body, _service.RetryOperationAsync, ct);

    [HttpPost("{jobId}/cancel")]
    public Task<StatRunOperationMutationResponse> Cancel(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Mutate(jobId, body, _service.CancelOperationAsync, ct);

    [HttpPost("{jobId}/reset")]
    public Task<StatRunOperationMutationResponse> Reset(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Mutate(jobId, body, _service.ResetOperationAsync, ct);

    [HttpPost("{jobId}/requeue")]
    public Task<StatRunOperationMutationResponse> Requeue(
        string jobId,
        [FromBody] JsonElement body,
        CancellationToken ct)
        => Mutate(jobId, body, _service.RequeueOperationAsync, ct);

    [HttpPost("cleanup/dry-run")]
    public Task<StatRunCleanupPreviewResponse> PreviewCleanup(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunCleanupPreviewRequest>(body);
        return _service.PreviewCleanupAsync(request, actor, ct);
    }

    [HttpPost("cleanup/apply")]
    public Task<StatRunCleanupApplyResponse> ApplyCleanup(
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunCleanupApplyRequest>(body);
        return _service.ApplyCleanupAsync(request, actor, ct);
    }

    private Task<StatRunOperationMutationResponse> Mutate(
        string jobId,
        JsonElement body,
        Func<string, StatRunOperationCommandRequest, tdtd_be.DTOs.Auth.MeResponse,
            CancellationToken, Task<StatRunOperationMutationResponse>> operation,
        CancellationToken ct)
    {
        // Coarse operator authorization deliberately precedes strict parsing,
        // identifier normalization, job existence and embedded receipt lookup.
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var request = StatRunCanonicalJson.DeserializeStrict<StatRunOperationCommandRequest>(body);
        return operation(jobId, request, actor, ct);
    }
}
