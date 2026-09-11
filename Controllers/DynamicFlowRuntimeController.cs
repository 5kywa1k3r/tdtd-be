using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class DynamicFlowRuntimeController : ControllerBase
{
    private readonly IDynamicFlowRuntimeService _service;
    private readonly IDynamicFlowRuntimeReadService _reads;
    private readonly IDynamicFlowPeriodicService _periodic;

    public DynamicFlowRuntimeController(
        IDynamicFlowRuntimeService service,
        IDynamicFlowRuntimeReadService reads,
        IDynamicFlowPeriodicService periodic)
    {
        _service = service;
        _reads = reads;
        _periodic = periodic;
    }

    [HttpPost("works/{workId}/dynamic-flows/preflight")]
    public async Task<ActionResult<DynamicFlowPreflightResponse>> Preflight(
        [FromRoute] string workId,
        [FromBody] DynamicFlowPreflightRequest req,
        [FromQuery] DynamicFlowPreflightPageQuery page,
        CancellationToken ct)
    {
        var result = await _service.PreflightAsync(
            workId,
            req ?? new DynamicFlowPreflightRequest(),
            GetActorUserId(),
            ct);
        return Ok(DynamicFlowRuntimeReadContract.PagePreflight(
            result,
            page ?? new DynamicFlowPreflightPageQuery()));
    }

    [HttpPost("works/{workId}/dynamic-flows/confirm")]
    public async Task<ActionResult<DynamicFlowConfirmResponse>> Confirm(
        [FromRoute] string workId,
        [FromBody] DynamicFlowConfirmRequest req,
        CancellationToken ct)
        => Ok(await _service.ConfirmAsync(
            workId,
            req ?? new DynamicFlowConfirmRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{parentInstanceId}/steps/{parentStepInstanceId}/subflow")]
    public async Task<ActionResult<DynamicFlowSubflowLaunchResponse>>
        LaunchSubflow(
            [FromRoute] string workId,
            [FromRoute] string parentInstanceId,
            [FromRoute] string parentStepInstanceId,
            [FromBody] DynamicFlowSubflowLaunchRequest req,
            CancellationToken ct)
        => Ok(await _service.LaunchSubflowAsync(
            workId,
            parentInstanceId,
            parentStepInstanceId,
            req ?? new DynamicFlowSubflowLaunchRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{flowInstanceId}/supplemental-steps")]
    public async Task<ActionResult<DynamicFlowSupplementalCommandResponse>>
        AddSupplementalStep(
            [FromRoute] string workId,
            [FromRoute] string flowInstanceId,
            [FromBody] DynamicFlowSupplementalAddRequest req,
            CancellationToken ct)
        => Ok(await _service.AddSupplementalStepAsync(
            workId,
            flowInstanceId,
            req ?? new DynamicFlowSupplementalAddRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{flowInstanceId}/supplemental-steps/{supplementalStepId}/cancel")]
    public async Task<ActionResult<DynamicFlowSupplementalCommandResponse>>
        CancelSupplementalStep(
            [FromRoute] string workId,
            [FromRoute] string flowInstanceId,
            [FromRoute] string supplementalStepId,
            [FromBody] DynamicFlowSupplementalCancelRequest req,
            CancellationToken ct)
        => Ok(await _service.CancelSupplementalStepAsync(
            workId,
            flowInstanceId,
            supplementalStepId,
            req ?? new DynamicFlowSupplementalCancelRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{flowInstanceId}/finalize")]
    public async Task<ActionResult<DynamicFlowEpochCommandResponse>>
        FinalizeEpoch(
            [FromRoute] string workId,
            [FromRoute] string flowInstanceId,
            [FromBody] DynamicFlowEpochCommandRequest req,
            CancellationToken ct)
        => Ok(await _service.FinalizeEpochAsync(
            workId,
            flowInstanceId,
            req ?? new DynamicFlowEpochCommandRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{flowInstanceId}/rollback")]
    public async Task<ActionResult<DynamicFlowEpochCommandResponse>>
        RollbackEpoch(
            [FromRoute] string workId,
            [FromRoute] string flowInstanceId,
            [FromBody] DynamicFlowEpochCommandRequest req,
            CancellationToken ct)
        => Ok(await _service.RollbackEpochAsync(
            workId,
            flowInstanceId,
            req ?? new DynamicFlowEpochCommandRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{flowInstanceId}/terminate")]
    public async Task<ActionResult<DynamicFlowEpochCommandResponse>>
        TerminateEpoch(
            [FromRoute] string workId,
            [FromRoute] string flowInstanceId,
            [FromBody] DynamicFlowEpochCommandRequest req,
            CancellationToken ct)
        => Ok(await _service.TerminateEpochAsync(
            workId,
            flowInstanceId,
            req ?? new DynamicFlowEpochCommandRequest(),
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/instances/{flowInstanceId}/restart")]
    public async Task<ActionResult<DynamicFlowEpochCommandResponse>>
        RestartEpoch(
            [FromRoute] string workId,
            [FromRoute] string flowInstanceId,
            [FromBody] DynamicFlowEpochCommandRequest req,
            CancellationToken ct)
        => Ok(await _service.RestartEpochAsync(
            workId,
            flowInstanceId,
            req ?? new DynamicFlowEpochCommandRequest(),
            GetActorUserId(),
            ct));

    [HttpPost("works/{workId}/dynamic-flows/instances")]
    public async Task<ActionResult<DynamicFlowInstanceLaunchResponse>> CreateInstance(
        [FromRoute] string workId,
        [FromBody] CreateDynamicFlowInstanceRequest req,
        CancellationToken ct)
    {
        var result = await _service.CreateInstanceAsync(
            workId,
            req ?? new CreateDynamicFlowInstanceRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpGet("works/{workId}/dynamic-flows/instances")]
    public async Task<ActionResult<
        DynamicFlowRuntimePageResponse<DynamicFlowRuntimeInstanceListRow>>> GetInstances(
        [FromRoute] string workId,
        [FromQuery] DynamicFlowRuntimeInstanceListQuery query,
        CancellationToken ct)
        => Ok(await _reads.GetInstancesAsync(
            workId,
            query ?? new DynamicFlowRuntimeInstanceListQuery(),
            GetActorUserId(),
            ct));

    [HttpPost("works/{workId}/dynamic-flows/periodic-schedules")]
    public async Task<ActionResult<DynamicFlowPeriodicScheduleDto>>
        CreatePeriodicSchedule(
            [FromRoute] string workId,
            [FromBody] DynamicFlowPeriodicScheduleCreateRequest req,
            CancellationToken ct)
        => Ok(await _periodic.CreateScheduleAsync(
            workId,
            req ?? new DynamicFlowPeriodicScheduleCreateRequest(),
            GetActorUserId(),
            ct));

    [HttpGet("works/{workId}/dynamic-flows/periodic-schedules")]
    public async Task<ActionResult<
        IReadOnlyList<DynamicFlowPeriodicScheduleDto>>>
        GetPeriodicSchedules(
            [FromRoute] string workId,
            CancellationToken ct)
        => Ok(await _periodic.GetSchedulesAsync(
            workId,
            GetActorUserId(),
            ct));

    [HttpGet(
        "works/{workId}/dynamic-flows/periodic-schedules/{scheduleId}/occurrences")]
    public async Task<ActionResult<
        IReadOnlyList<DynamicFlowPeriodicOccurrenceDto>>>
        GetPeriodicOccurrences(
            [FromRoute] string workId,
            [FromRoute] string scheduleId,
            CancellationToken ct)
        => Ok(await _periodic.GetOccurrencesAsync(
            workId,
            scheduleId,
            GetActorUserId(),
            ct));

    [HttpPost(
        "works/{workId}/dynamic-flows/periodic-schedules/{scheduleId}/occurrences/{periodKey}/rerun")]
    public async Task<ActionResult<DynamicFlowPeriodicOccurrenceDto>>
        RerunPeriodicOccurrence(
            [FromRoute] string workId,
            [FromRoute] string scheduleId,
            [FromRoute] string periodKey,
            [FromBody] DynamicFlowPeriodicManualRerunRequest req,
            CancellationToken ct)
        => Ok(await _periodic.ManualRerunAsync(
            workId,
            scheduleId,
            periodKey,
            req ?? new DynamicFlowPeriodicManualRerunRequest(),
            GetActorUserId(),
            ct));

    [HttpGet("works/{workId}/dynamic-flows/instances/{flowInstanceId}")]
    public async Task<ActionResult<DynamicFlowRuntimeInstanceOverview>> GetInstance(
        [FromRoute] string workId,
        [FromRoute] string flowInstanceId,
        CancellationToken ct)
        => Ok(await _reads.GetInstanceAsync(
            workId,
            flowInstanceId,
            GetActorUserId(),
            ct));

    [HttpGet("works/{workId}/dynamic-flows/instances/{flowInstanceId}/steps")]
    public async Task<ActionResult<
        DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>>> GetSteps(
        [FromRoute] string workId,
        [FromRoute] string flowInstanceId,
        [FromQuery] DynamicFlowRuntimePageQuery query,
        CancellationToken ct)
        => Ok(await _reads.GetStepsAsync(
            workId,
            flowInstanceId,
            query ?? new DynamicFlowRuntimePageQuery(),
            GetActorUserId(),
            ct));

    [HttpGet("dynamic-flows/inbox")]
    public async Task<ActionResult<
        DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>>> GetInbox(
        [FromQuery] DynamicFlowRuntimeInboxQuery query,
        CancellationToken ct)
        => Ok(await _reads.GetInboxAsync(
            query ?? new DynamicFlowRuntimeInboxQuery(),
            GetActorUserId(),
            ct));

    [HttpGet("works/{workId}/dynamic-flows/instances/{flowInstanceId}/timeline")]
    public async Task<ActionResult<
        DynamicFlowRuntimePageResponse<DynamicFlowRuntimeTimelineRow>>> GetTimeline(
        [FromRoute] string workId,
        [FromRoute] string flowInstanceId,
        [FromQuery] DynamicFlowRuntimePageQuery query,
        CancellationToken ct)
        => Ok(await _reads.GetTimelineAsync(
            workId,
            flowInstanceId,
            query ?? new DynamicFlowRuntimePageQuery(),
            GetActorUserId(),
            ct));

    [HttpGet("works/{workId}/dynamic-flows/instances/{flowInstanceId}/recovery")]
    public async Task<ActionResult<DynamicFlowRuntimeRecoveryStatusDto>> GetRecovery(
        [FromRoute] string workId,
        [FromRoute] string flowInstanceId,
        CancellationToken ct)
        => Ok(await _reads.GetRecoveryStatusAsync(
            workId,
            flowInstanceId,
            GetActorUserId(),
            ct));

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/rollback")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> RollbackBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.RollbackBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/terminate")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> TerminateBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.TerminateBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/restart")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> RestartBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.RestartBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    [HttpPost("works/{workId}/dynamic-flows/assignments/{assignmentId}/forward")]
    public async Task<ActionResult<DynamicFlowBranchActionResponse>> ForwardBranch(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] DynamicFlowBranchActionRequest req,
        CancellationToken ct)
    {
        var result = await _service.ForwardBranchAsync(
            workId,
            assignmentId,
            req ?? new DynamicFlowBranchActionRequest(),
            GetActorUserId(),
            ct);
        return Ok(result);
    }

    private string GetActorUserId()
        => User.FindFirstValue("sub") ?? throw AppExceptionFactory.Unauthorized();
}
