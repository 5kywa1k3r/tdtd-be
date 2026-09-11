using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.Controllers;

/// <summary>
/// Crash-recovery seam for the only safe intermediate state: an already
/// committed pending generation. It accepts no semantic evidence or owner pin.
/// </summary>
[ApiController]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/admin/internal/statistics-reconciliation")]
public sealed class StatisticReconciliationPendingFinalizationController(
    MeAccessor me,
    MongoDbContext context,
    IStatisticReconciliationCandidateActivation activation,
    IServiceProvider services) : ControllerBase
{
    [HttpPost("{reconciliationId}/finalize-pending")]
    public async Task<IActionResult> FinalizePendingAsync(
        [FromRoute] string reconciliationId,
        CancellationToken cancellationToken = default)
    {
        var actor = me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        _ = activation.RequireFoundation(
            StatisticReconciliationCapabilities.ExpectedActualDelta,
            StatisticReconciliationRouteRegistry.WorkerFinalize);
        if (!ObjectId.TryParse(reconciliationId, out _))
            throw HiddenNotFound();

        // Authorization and activation have completed before target existence.
        var run = await context.StatisticReconciliationRuns
            .Find(value => value.Id == reconciliationId && !value.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw HiddenNotFound();
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        string actualGenerationId;
        string actualGenerationHash;
        if (run.PendingGenerationId is not null &&
            run.PendingGenerationHash is not null &&
            run.PendingGenerationPublishedAtUtc.HasValue)
        {
            actualGenerationId = run.PendingGenerationId;
            actualGenerationHash = run.PendingGenerationHash;
        }
        else if (run.PendingGenerationId is null &&
                 run.PendingGenerationHash is null &&
                 !run.PendingGenerationPublishedAtUtc.HasValue &&
                 run.CurrentGenerationId is not null &&
                 run.CurrentGenerationHash is not null)
        {
            // The trusted pipeline admits current replay only for either an
            // initial generation with one exact FINAL_VERDICT, or a recheck
            // successor protected by its durable finalize receipt.
            actualGenerationId = run.CurrentGenerationId;
            actualGenerationHash = run.CurrentGenerationHash;
        }
        else
        {
            throw PendingConflict();
        }

        var command = StatisticReconciliationTrustedVerdictPipeline.CreateCommand(
            run.Id,
            actualGenerationId,
            actualGenerationHash,
            run.StateRevision,
            run.StateHash,
            run.GenerationPublishRevision);
        var pipeline = services.GetRequiredService<
            IStatisticReconciliationTrustedVerdictPipeline>();
        var result = await pipeline.FinalizeAsync(
                command,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(new
        {
            result.IsReplay,
            result.ReconciliationId,
            result.Status,
            result.StateRevision,
            result.StateHash,
            result.GenerationPublishRevision,
            result.CurrentGenerationId,
            result.CurrentGenerationHash,
            VerdictGenerationId = result.Verdict.VerdictGenerationId
        });
    }

    private static AppException HiddenNotFound()
        => new(AppErrorCode.STAT_RECONCILIATION_NOT_FOUND,
            new { writes = 0 });

    private static AppException PendingConflict()
        => new(AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT,
            new { reason = "PENDING_FINALIZATION_NOT_AVAILABLE", writes = 0 });
}
