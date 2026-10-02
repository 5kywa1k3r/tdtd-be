using System.Security.Claims;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/aggregate-v2/preview-jobs"), RequestSizeLimit(524288)]
public sealed class AggregatePreviewJobsController(MongoDbContext db, IWorkReportPayloadReader payloads, IConfiguration configuration) : ControllerBase
{
    [HttpPost("current")]
    public Task<IActionResult> Current(AggregateInstanceReadCommandDto request, CancellationToken ct)
        => Run(async (service, actor, session, jobs, token) => {
            var id = await service.Current(request.Context, actor, session, token);
            var completedId = await service.Current(request.Context, actor, session, token, completedOnly: true);
            var value = id == null ? null : await service.Read(id, actor, session, token);
            var completed = completedId == null || completedId == id ? null : await service.Read(completedId, actor, session, token);
            if (id != null) await service.Enqueue(id, jobs, token);
            return new { Current = value, LastCompleted = completed };
        }, ct);
    [HttpPost("start")]
    public Task<IActionResult> Start(AggregatePreviewJobRequest request, CancellationToken ct)
        => Run(async (service, actor, session, jobs, token) => {
            var id = await service.Start(request, actor, session, token);
            await service.Enqueue(id, jobs, token);
            return await service.Read(id, actor, session, token);
        }, ct);
    [HttpPost("{id}/read")]
    public Task<IActionResult> Read(string id, CancellationToken ct)
        => Run(async (service, actor, session, jobs, token) => {
            var result = await service.Read(id, actor, session, token);
            await service.Enqueue(id, jobs, token);
            return result;
        }, ct);
    [HttpPost("{id}/cancel")]
    public Task<IActionResult> Cancel(string id, CancellationToken ct)
        => Run(async (service, actor, session, _, token) => {
            await service.Cancel(id, actor, session, token);
            return await service.Read(id, actor, session, token);
        }, ct);
    private async Task<IActionResult> Run(Func<AggregatePreviewJobs, string, string, IBackgroundJobClient, CancellationToken, Task<object>> action, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AggregateIntegrationGate.V2Ready(configuration)) return Conflict(new { code = "AGG_ACTIVATION_REQUIRED" });
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var session = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(session)) return Unauthorized();
        var jobs = HttpContext.RequestServices.GetService<IBackgroundJobClient>();
        if (jobs == null) return StatusCode(503, new { code = "AGG_JOB_EXECUTOR_UNAVAILABLE" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { return Ok(await action(new(db, payloads, configuration), actor, session, jobs, timeout.Token)); }
        catch (AggregatePreviewException ex) { return StatusCode(ex.Code.Contains("UNAVAILABLE") ? 404 : ex.Code.Contains("FORBIDDEN") ? 403 : 409, new { code = ex.Code }); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(503, new { code = "AGG_PREVIEW_TIMEOUT" }); }
    }
}
