using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Common.Auth;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/operations/dynamic-flow-runtime")]
public sealed class DynamicFlowRuntimeOperationsController : ControllerBase
{
    private readonly MeAccessor _me;
    private readonly IDynamicFlowRuntimeMaterializer _materializer;
    private readonly IDynamicFlowPeriodicService _periodic;
    private readonly IWorkStatusOperationLogService _operationLogs;

    public DynamicFlowRuntimeOperationsController(
        MeAccessor me,
        IDynamicFlowRuntimeMaterializer materializer,
        IDynamicFlowPeriodicService periodic,
        IWorkStatusOperationLogService operationLogs)
    {
        _me = me;
        _materializer = materializer;
        _periodic = periodic;
        _operationLogs = operationLogs;
    }

    [HttpPost("periodic/process")]
    public async Task<IActionResult> ProcessPeriodic(
        [FromBody] tdtd_be.DTOs.DynamicFlows.DynamicFlowPeriodicProcessRequest req,
        CancellationToken ct = default)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        req ??= new tdtd_be.DTOs.DynamicFlows.DynamicFlowPeriodicProcessRequest();
        return Ok(await _periodic.ProcessDueAsync(
            req.ObservedAtUtc ?? DateTime.UtcNow,
            req.MaxItems,
            ct));
    }

    [HttpPost("outbox/process")]
    public async Task<IActionResult> ProcessOutbox(
        [FromQuery] int maxItems = 20,
        CancellationToken ct = default)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var processed = await _materializer.ProcessPendingAsync(
            Math.Clamp(maxItems, 1, 200),
            ct);
        return Ok(new
        {
            processed,
            maxItems = Math.Clamp(maxItems, 1, 200)
        });
    }

    [HttpPost("instances/{flowInstanceId}/reconcile")]
    public async Task<IActionResult> Reconcile(
        [FromRoute] string flowInstanceId,
        [FromQuery] bool apply = false,
        CancellationToken ct = default)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var startedAtUtc = DateTime.UtcNow;
        try
        {
            var result = await _materializer.ReconcileAsync(
                flowInstanceId,
                apply,
                actor.Id,
                ct);
            await WriteAuditAsync(
                "DYNAMIC_FLOW_RUNTIME_RECONCILE",
                flowInstanceId,
                apply,
                actor.Id,
                startedAtUtc,
                JsonSerializer.Serialize(result),
                null,
                ct);
            return Ok(result);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await WriteAuditAsync(
                "DYNAMIC_FLOW_RUNTIME_RECONCILE",
                flowInstanceId,
                apply,
                actor.Id,
                startedAtUtc,
                null,
                error,
                ct);
            throw;
        }
    }

    [HttpPost("instances/{flowInstanceId}/compensate")]
    public async Task<IActionResult> Compensate(
        [FromRoute] string flowInstanceId,
        [FromQuery] bool apply = false,
        CancellationToken ct = default)
    {
        var actor = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(actor);
        var startedAtUtc = DateTime.UtcNow;
        try
        {
            var affected = await _materializer.CompensateOwnedArtifactsAsync(
                flowInstanceId,
                apply,
                actor.Id,
                ct);
            var result = new
            {
                flowInstanceId,
                apply,
                affectedOwnedArtifacts = affected
            };
            await WriteAuditAsync(
                "DYNAMIC_FLOW_RUNTIME_COMPENSATE",
                flowInstanceId,
                apply,
                actor.Id,
                startedAtUtc,
                JsonSerializer.Serialize(result),
                null,
                ct);
            return Ok(result);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await WriteAuditAsync(
                "DYNAMIC_FLOW_RUNTIME_COMPENSATE",
                flowInstanceId,
                apply,
                actor.Id,
                startedAtUtc,
                null,
                error,
                ct);
            throw;
        }
    }

    private Task WriteAuditAsync(
        string operation,
        string flowInstanceId,
        bool apply,
        string actorUserId,
        DateTime startedAtUtc,
        string? details,
        Exception? error,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        var result = error is not null ? "FAILED" : apply ? "SUCCESS" : "DRY_RUN";
        var lifecycleFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{operation}\n{flowInstanceId}\n{apply}\n{actorUserId}\n{result}\n{details}\n{error?.GetType().FullName}\n{error?.Message}")))
            .ToLowerInvariant();
        var lifecycleEventKey =
            $"dynamic-flow-runtime:{operation}:{flowInstanceId}:{lifecycleFingerprint}";
        return _operationLogs.WriteIdempotentAsync(lifecycleEventKey, new WorkStatusOperationLog
        {
            Operation = operation,
            Scope = $"dynamic-flow-runtime:{flowInstanceId}",
            Result = result,
            ActorUserId = actorUserId,
            Summary = details,
            ErrorType = error?.GetType().FullName,
            ErrorMessage = error?.Message,
            ErrorStackTrace = error?.ToString(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = Math.Max(0, (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
            CreatedAtUtc = completedAtUtc,
            UpdatedAtUtc = completedAtUtc,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        }, ct);
    }
}
