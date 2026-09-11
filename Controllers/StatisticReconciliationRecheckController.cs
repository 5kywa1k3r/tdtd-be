using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
public sealed class StatisticReconciliationRecheckController(
    IStatisticReconciliationRunService service,
    MeAccessor me) : ControllerBase
{
    [HttpPost(
        "api/works/{workId}/statistics/{scopeAssignmentId}/reconciliations/" +
        "{reconciliationId}/recheck")]
    [ProducesResponseType<StatisticReconciliationBeginRecheckResponse>(
        StatusCodes.Status202Accepted)]
    public async Task<ActionResult<StatisticReconciliationBeginRecheckResponse>>
        Begin(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        CancellationToken ct)
    {
        var response = await service.BeginRecheckAsync(workId,
            scopeAssignmentId, reconciliationId, Request.Body,
            me.RequireMe(), ct);
        return Accepted(response);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost(
        "api/admin/internal/p10/statistic-reconciliations/jobs/" +
        "{reconciliationId}/recheck/remediation/authorize")]
    public async Task<ActionResult<
        StatisticReconciliationRemediationEvidenceResponse>>
        AuthorizeRemediation(
            string reconciliationId,
            CancellationToken ct)
    {
        var actor = me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        return Ok(await service.AuthorizeRecheckRemediationAsync(
            reconciliationId, actor, ct));
    }
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost(
        "api/admin/internal/p10/statistic-reconciliations/jobs/" +
        "{reconciliationId}/recheck/claim")]
    public async Task<ActionResult<StatisticReconciliationWorkerLeaseResponse>>
        Claim(
            string reconciliationId,
            CancellationToken ct)
    {
        var actor = me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        return Ok(await service.ClaimRecheckAsync(reconciliationId,
            Request.Body, actor, ct));
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost(
        "api/admin/internal/p10/statistic-reconciliations/jobs/" +
        "{reconciliationId}/recheck/heartbeat")]
    public async Task<ActionResult<StatisticReconciliationDetailResponse>>
        Heartbeat(
            string reconciliationId,
            CancellationToken ct)
    {
        var actor = me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        return Ok(await service.HeartbeatRecheckAsync(
            reconciliationId, Request.Body, actor, ct));
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost(
        "api/admin/internal/p10/statistic-reconciliations/jobs/" +
        "{reconciliationId}/recheck/fail")]
    public async Task<ActionResult<StatisticReconciliationDetailResponse>>
        Fail(
            string reconciliationId,
            CancellationToken ct)
    {
        var actor = me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        return Ok(await service.FailRecheckAsync(
            reconciliationId, Request.Body, actor, ct));
    }
}