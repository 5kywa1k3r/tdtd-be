using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Services.WorkAssignments.Progress;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api")]
public sealed class WorkCompletionController(WorkCompletionWorkflowService workflow, MeAccessor me) : ControllerBase
{
    [HttpGet("works/{workId}/completion")]
    public async Task<IActionResult> ReadWorkCompletion(string workId, CancellationToken ct)
        => Ok(await workflow.ReadWorkCompletionAsync(workId, me.RequireMe().Id, ct));

    [HttpPost("works/{workId}/completion/reopen")]
    public async Task<IActionResult> ReopenWork(string workId, WorkCompletionReopenCommand command, CancellationToken ct)
        => Ok(await workflow.ReopenWorkAsync(workId, me.RequireMe().Id, command, ct));

    [HttpGet("work-assignments/{id}/completion")]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
        => Ok(await workflow.ReadAsync(id, me.RequireMe().Id, ct));

    [HttpGet("works/{workId}/assignment-completions")]
    public async Task<IActionResult> ReadWork(string workId, CancellationToken ct)
        => Ok(await workflow.ReadWorkAsync(workId, me.RequireMe().Id, ct));

    [HttpPost("work-assignments/{id}/completion/requests")]
    public async Task<IActionResult> RequestCompletion(string id, CompletionRequestCommand command, CancellationToken ct)
        => Ok(await workflow.RequestAsync(id, me.RequireMe().Id, command, ct));

    [HttpPost("work-assignments/{id}/completion/requests/{requestId}/decision")]
    public async Task<IActionResult> Decide(string id, string requestId, CompletionDecisionCommand command, CancellationToken ct)
        => Ok(await workflow.DecideAsync(id, requestId, me.RequireMe().Id, command, ct));

    [HttpPost("work-assignments/{id}/completion/reopen")]
    public async Task<IActionResult> Reopen(string id, CompletionReopenCommand command, CancellationToken ct)
        => Ok(await workflow.ReopenAsync(id, me.RequireMe().Id, command, ct));
}
