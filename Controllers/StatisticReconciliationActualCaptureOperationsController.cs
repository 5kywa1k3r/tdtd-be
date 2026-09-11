using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using tdtd_be.Common.Auth;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.Controllers;

public sealed record StatisticReconciliationActualClaimedCaptureHttpRequest(
    string WorkerId,
    string ClaimToken);

/// <summary>
/// System-admin control-plane seam for an already claimed reconciliation run.
/// The request intentionally accepts no owner ids, hashes, filters, slices,
/// projections, or publication material.
/// </summary>
[ApiController]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/admin/internal/statistics-reconciliation")]
public sealed class StatisticReconciliationActualCaptureOperationsController(
    MeAccessor me,
    IServiceProvider services,
    IStatisticReconciliationCandidateActivation activation) : ControllerBase
{
    [HttpPost("{reconciliationId}/actual-capture/claimed")]
    public async Task<IActionResult> CaptureClaimedAsync(
        [FromRoute] string reconciliationId,
        CancellationToken cancellationToken = default)
    {
        var actor = me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var body = await StatisticReconciliationBoundedJsonBody.ReadAsync(
                Request.Body,
                cancellationToken)
            .ConfigureAwait(false);
        var request = StatisticReconciliationCanonicalJson.DeserializeStrict<
            StatisticReconciliationActualClaimedCaptureHttpRequest>(body);

        return await StatisticReconciliationActualWorkerCaptureActivationGate
            .ExecuteAsync<IActionResult>(
                activation,
                async ct =>
                {
                    var worker = services.GetRequiredService<
                        IStatisticReconciliationActualClaimedCaptureWorker>();
                    var outcome = await worker.CaptureAsync(
                            reconciliationId,
                            request.WorkerId,
                            request.ClaimToken,
                            actor,
                            ct)
                        .ConfigureAwait(false);

                    return Ok(new
                    {
                        outcome.Generation.ReconciliationId,
                        outcome.Generation.GenerationId,
                        outcome.Generation.CaptureState,
                        outcome.Generation.StaleReason,
                        outcome.Published
                    });
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
