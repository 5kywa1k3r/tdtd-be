using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/operations")]
public sealed class AdminOperationsController : ControllerBase
{
    private readonly MeAccessor _me;
    private readonly IUserActionLogService _userActionLogs;
    private readonly IWorkStatusOperationLogService _operationLogs;
    private readonly IJobRunManagementService _jobRuns;
    private readonly IWorkReportPayloadDiagnosticsService _payloadDiagnostics;

    public AdminOperationsController(
        MeAccessor me,
        IUserActionLogService userActionLogs,
        IWorkStatusOperationLogService operationLogs,
        IJobRunManagementService jobRuns,
        IWorkReportPayloadDiagnosticsService payloadDiagnostics)
    {
        _me = me;
        _userActionLogs = userActionLogs;
        _operationLogs = operationLogs;
        _jobRuns = jobRuns;
        _payloadDiagnostics = payloadDiagnostics;
    }

    [HttpGet("action-logs")]
    public async Task<IActionResult> SearchActionLogs(
        [FromQuery] string? action = null,
        [FromQuery] string? scope = null,
        [FromQuery] string? result = null,
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? workReportPeriodId = null,
        [FromQuery] string? workAssignmentReportId = null,
        [FromQuery] string? actorUserId = null,
        [FromQuery] string? unitId = null,
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var rows = await _userActionLogs.SearchAsync(
            new UserActionLogSearchRequest
            {
                Action = action,
                Scope = scope,
                Result = result,
                WorkId = workId,
                WorkAssignmentId = workAssignmentId,
                WorkReportPeriodId = workReportPeriodId,
                WorkAssignmentReportId = workAssignmentReportId,
                ActorUserId = actorUserId,
                UnitId = unitId,
                UserId = userId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Query = q,
                Page = page,
                PageSize = pageSize
            },
            me,
            ct);

        return Ok(rows);
    }

    [HttpGet("action-logs/{id}")]
    public async Task<IActionResult> GetActionLog(string id, CancellationToken ct)
    {
        var row = await _userActionLogs.GetByIdAsync(id, _me.RequireMe(), ct);
        return row is null ? NotFound() : Ok(row);
    }

    [HttpGet("job-runs/operation-logs")]
    public async Task<IActionResult> SearchJobOperationLogs(
        [FromQuery] string? operation = null,
        [FromQuery] string? scope = null,
        [FromQuery] string? result = null,
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? workReportPeriodId = null,
        [FromQuery] string? workAssignmentReportId = null,
        [FromQuery] string? actorUserId = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool includeStackTrace = false,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        var rows = await _operationLogs.SearchAsync(
            new WorkStatusOperationLogSearchRequest
            {
                Operation = operation,
                Scope = scope,
                Result = result,
                WorkId = workId,
                WorkAssignmentId = workAssignmentId,
                WorkReportPeriodId = workReportPeriodId,
                WorkAssignmentReportId = workAssignmentReportId,
                ActorUserId = actorUserId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Query = q,
                Page = page,
                PageSize = pageSize,
                IncludeStackTrace = includeStackTrace
            },
            ct);

        return Ok(rows);
    }

    [HttpGet("job-runs/operation-logs/{id}")]
    public async Task<IActionResult> GetJobOperationLog(
        string id,
        [FromQuery] bool includeStackTrace = true,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        var row = await _operationLogs.GetByIdAsync(id, includeStackTrace, ct);
        return row is null ? NotFound() : Ok(row);
    }

    [HttpGet("job-runs/materialize-jobs")]
    public async Task<IActionResult> SearchMaterializeJobs(
        [FromQuery] string? status = null,
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _jobRuns.SearchMaterializeJobsAsync(new JobRunSearchRequest
        {
            Status = status,
            WorkId = workId,
            WorkAssignmentId = workAssignmentId,
            Query = q,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        }, ct));
    }

    [HttpPost("job-runs/materialize-jobs/process")]
    public async Task<IActionResult> ProcessMaterializeJobs(
        [FromQuery] int maxJobs = 10,
        [FromQuery] int batchSize = 20,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        var processed = await _jobRuns.ProcessMaterializeJobsAsync(maxJobs, batchSize, ct);
        return Ok(new
        {
            ok = true,
            processed,
            maxJobs = Math.Clamp(maxJobs, 1, 50),
            batchSize = Math.Clamp(batchSize, 1, 200)
        });
    }

    [HttpPost("job-runs/queue-daily-scan/process")]
    public async Task<IActionResult> ProcessQueueDailyScan(CancellationToken ct = default)
    {
        RequireSystemAdmin();

        await _jobRuns.ProcessWorkAssignmentQueueScanAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpPost("job-runs/notification-due-scan/process")]
    public async Task<IActionResult> ProcessNotificationDueScan(CancellationToken ct = default)
    {
        RequireSystemAdmin();

        await _jobRuns.ProcessNotificationDueScanAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpGet("job-runs/projection-retry-jobs")]
    public async Task<IActionResult> SearchProjectionRetryJobs(
        [FromQuery] string? status = null,
        [FromQuery] string? action = null,
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? workReportPeriodId = null,
        [FromQuery] string? userId = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _jobRuns.SearchProjectionRetryJobsAsync(new JobRunSearchRequest
        {
            Status = status,
            Action = action,
            WorkId = workId,
            WorkAssignmentId = workAssignmentId,
            WorkReportPeriodId = workReportPeriodId,
            UserId = userId,
            Query = q,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        }, ct));
    }

    [HttpPost("job-runs/projection-retry-jobs/process")]
    public async Task<IActionResult> ProcessProjectionRetryJobs(
        [FromQuery] int maxJobs = 20,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        var processed = await _jobRuns.ProcessProjectionRetryJobsAsync(maxJobs, ct);
        return Ok(new { ok = true, processed, maxJobs = Math.Clamp(maxJobs, 1, 200) });
    }

    [HttpGet("job-runs/action-log-retry-jobs")]
    public async Task<IActionResult> SearchActionLogRetryJobs(
        [FromQuery] string? status = null,
        [FromQuery] string? action = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _userActionLogs.SearchRetryJobsAsync(new JobRunSearchRequest
        {
            Status = status,
            Action = action,
            Query = q,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        }, ct));
    }

    [HttpPost("job-runs/action-log-retry-jobs/process")]
    public async Task<IActionResult> ProcessActionLogRetryJobs(
        [FromQuery] int maxJobs = 20,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        var processed = await _jobRuns.ProcessUserActionLogRetriesAsync(maxJobs, ct);
        return Ok(new { ok = true, processed, maxJobs = Math.Clamp(maxJobs, 1, 200) });
    }

    [HttpGet("job-runs/statistic-rebuild-jobs")]
    public async Task<IActionResult> SearchStatisticRebuildJobs(
        [FromQuery] string? status = null,
        [FromQuery] string? dynamicFormTemplateId = null,
        [FromQuery] string? userId = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _jobRuns.SearchStatisticRebuildJobsAsync(new JobRunSearchRequest
        {
            Status = status,
            DynamicFormTemplateId = dynamicFormTemplateId,
            UserId = userId,
            Query = q,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        }, ct));
    }

    [HttpPost("job-runs/statistic-rebuild-jobs/process")]
    public async Task<IActionResult> ProcessStatisticRebuildJobs(
        [FromQuery] int maxJobs = 3,
        [FromQuery] int batchSize = 25,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        var processed = await _jobRuns.ProcessStatisticRebuildJobsAsync(maxJobs, batchSize, ct);
        return Ok(new
        {
            ok = true,
            processed,
            maxJobs = Math.Clamp(maxJobs, 1, 20),
            batchSize = Math.Clamp(batchSize, 1, 100)
        });
    }

    [HttpGet("job-runs/basic-summary-jobs")]
    public async Task<IActionResult> SearchBasicSummaryJobs(
        [FromQuery] string? status = null,
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? dynamicFormTemplateId = null,
        [FromQuery] string? userId = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _jobRuns.SearchBasicSummaryJobsAsync(new JobRunSearchRequest
        {
            Status = status,
            WorkId = workId,
            WorkAssignmentId = workAssignmentId,
            DynamicFormTemplateId = dynamicFormTemplateId,
            UserId = userId,
            Query = q,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        }, ct));
    }

    [HttpPost("job-runs/basic-summary-jobs/{snapshotId}/reset")]
    public async Task<IActionResult> ResetBasicSummaryJob(
        string snapshotId,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);

        return Ok(await _jobRuns.ResetBasicSummaryJobAsync(snapshotId, me.Id, ct));
    }

    [HttpGet("job-runs/advanced-summary-nodes")]
    public async Task<IActionResult> SearchAdvancedSummaryNodes(
        [FromQuery] string? status = null,
        [FromQuery] string? grain = null,
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? dynamicFormTemplateId = null,
        [FromQuery] string? sectionId = null,
        [FromQuery] string? configId = null,
        [FromQuery] string? configHash = null,
        [FromQuery] string? q = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _jobRuns.SearchAdvancedSummaryNodesAsync(new JobRunSearchRequest
        {
            Status = status,
            Grain = grain,
            WorkId = workId,
            WorkAssignmentId = workAssignmentId,
            DynamicFormTemplateId = dynamicFormTemplateId,
            SectionId = sectionId,
            ConfigId = configId,
            ConfigHash = configHash,
            Query = q,
            IncludeInactive = includeInactive,
            Page = page,
            PageSize = pageSize
        }, ct));
    }

    [HttpPost("job-runs/advanced-summary-nodes/{grain}/{nodeId}/reset")]
    public async Task<IActionResult> ResetAdvancedSummaryNode(
        string grain,
        string nodeId,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);

        return Ok(await _jobRuns.ResetAdvancedSummaryNodeAsync(grain, nodeId, me.Id, ct));
    }

    [HttpPost("job-runs/advanced-summary-nodes/cleanup")]
    public async Task<IActionResult> CleanupAdvancedSummaryNodes(
        [FromBody] AdvancedSummaryNodeCleanupRequest? request,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);
        request ??= new AdvancedSummaryNodeCleanupRequest();
        var startedAtUtc = DateTime.UtcNow;

        try
        {
            var result = await _jobRuns.CleanupAdvancedSummaryNodesAsync(request, me.Id, ct);
            await WriteAdvancedSummaryNodeCleanupLogAsync(request, result, startedAtUtc, me.Id, ex: null, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            await WriteAdvancedSummaryNodeCleanupLogAsync(request, result: null, startedAtUtc, me.Id, ex, ct);
            throw;
        }
    }

    [HttpPost("job-runs/advanced-summary-nodes/diagnostics/day")]
    public async Task<IActionResult> DiagnoseAdvancedSummaryDayNode(
        [FromBody] DiagnoseWorkAssignmentAdvancedSummaryDayNodeRequest? request,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);
        request ??= new DiagnoseWorkAssignmentAdvancedSummaryDayNodeRequest();

        return Ok(await _jobRuns.DiagnoseAdvancedSummaryDayNodeAsync(
            request.ConfigId,
            request.DayKey,
            request,
            me.Id,
            ct));
    }

    [HttpPost("job-runs/advanced-summary-nodes/diagnostics/month")]
    public async Task<IActionResult> DiagnoseAdvancedSummaryMonthNode(
        [FromBody] DiagnoseWorkAssignmentAdvancedSummaryMonthNodeRequest? request,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);
        request ??= new DiagnoseWorkAssignmentAdvancedSummaryMonthNodeRequest();

        return Ok(await _jobRuns.DiagnoseAdvancedSummaryMonthNodeAsync(
            request.ConfigId,
            request.MonthKey,
            request,
            me.Id,
            ct));
    }

    [HttpPost("job-runs/advanced-summary-nodes/diagnostics/year")]
    public async Task<IActionResult> DiagnoseAdvancedSummaryYearNode(
        [FromBody] DiagnoseWorkAssignmentAdvancedSummaryYearNodeRequest? request,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);
        request ??= new DiagnoseWorkAssignmentAdvancedSummaryYearNodeRequest();

        return Ok(await _jobRuns.DiagnoseAdvancedSummaryYearNodeAsync(
            request.ConfigId,
            request.YearKey,
            request,
            me.Id,
            ct));
    }

    [HttpGet("report-payloads/diagnostics")]
    public async Task<IActionResult> CheckReportPayloadDiagnostics(
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? workReportPeriodId = null,
        [FromQuery] string? workAssignmentReportId = null,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        RequireSystemAdmin();

        return Ok(await _payloadDiagnostics.CheckAsync(
            new WorkReportPayloadDiagnosticsOptions
            {
                WorkId = workId,
                WorkAssignmentId = workAssignmentId,
                WorkReportPeriodId = workReportPeriodId,
                WorkAssignmentReportId = workAssignmentReportId,
                Limit = limit
            },
            ct));
    }

    [HttpPost("report-payloads/diagnostics/repair")]
    public async Task<IActionResult> RepairReportPayloadDiagnostics(
        [FromQuery] string? workId = null,
        [FromQuery] string? workAssignmentId = null,
        [FromQuery] string? workReportPeriodId = null,
        [FromQuery] string? workAssignmentReportId = null,
        [FromQuery] bool dryRun = true,
        [FromQuery] bool softDeleteOrphanPayloadRows = true,
        [FromQuery] bool softDeleteOrphanTableValueRows = true,
        [FromQuery] bool enqueueStatisticRebuilds = true,
        [FromQuery] bool highPriorityStatisticRebuilds = true,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);
        var startedAtUtc = DateTime.UtcNow;

        try
        {
            var result = await _payloadDiagnostics.RepairAsync(
                new WorkReportPayloadDiagnosticsRepairOptions
                {
                    WorkId = workId,
                    WorkAssignmentId = workAssignmentId,
                    WorkReportPeriodId = workReportPeriodId,
                    WorkAssignmentReportId = workAssignmentReportId,
                    DryRun = dryRun,
                    SoftDeleteOrphanPayloadRows = softDeleteOrphanPayloadRows,
                    SoftDeleteOrphanTableValueRows = softDeleteOrphanTableValueRows,
                    EnqueueStatisticRebuilds = enqueueStatisticRebuilds,
                    HighPriorityStatisticRebuilds = highPriorityStatisticRebuilds,
                    ByUserId = me.Id,
                    Limit = limit
                },
                ct);

            await WriteReportPayloadDiagnosticsRepairLogAsync(
                result,
                startedAtUtc,
                me.Id,
                ex: null,
                ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            await WriteReportPayloadDiagnosticsRepairLogAsync(
                new WorkReportPayloadDiagnosticsRepairResult
                {
                    DryRun = dryRun,
                    Limit = limit,
                    SoftDeleteOrphanPayloadRows = softDeleteOrphanPayloadRows,
                    SoftDeleteOrphanTableValueRows = softDeleteOrphanTableValueRows,
                    EnqueueStatisticRebuilds = enqueueStatisticRebuilds,
                    HighPriorityStatisticRebuilds = highPriorityStatisticRebuilds,
                    Diagnostics = new WorkReportPayloadDiagnosticsResult
                    {
                        WorkId = workId,
                        WorkAssignmentId = workAssignmentId,
                        WorkReportPeriodId = workReportPeriodId,
                        WorkAssignmentReportId = workAssignmentReportId,
                        Limit = limit
                    }
                },
                startedAtUtc,
                me.Id,
                ex,
                ct);

            throw;
        }
    }

    private void RequireSystemAdmin()
        => RoleGuard.RequireSystemAdmin(_me.RequireMe());

    private Task WriteAdvancedSummaryNodeCleanupLogAsync(
        AdvancedSummaryNodeCleanupRequest request,
        AdvancedSummaryNodeCleanupResponse? result,
        DateTime startedAtUtc,
        string actorUserId,
        Exception? ex,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        var logResult = ex is not null
            ? "FAILED"
            : result?.DryRun == true
                ? "DRY_RUN"
                : "SUCCESS";

        return _operationLogs.WriteAsync(new WorkStatusOperationLog
        {
            Operation = "ADVANCED_SUMMARY_NODE_CLEANUP",
            Scope = "advanced-summary-nodes",
            Result = logResult,
            WorkId = request.WorkId,
            WorkAssignmentId = request.WorkAssignmentId,
            ActorUserId = actorUserId,
            Summary = BuildAdvancedSummaryNodeCleanupSummary(request, result),
            ErrorType = ex?.GetType().FullName,
            ErrorMessage = ex?.Message,
            ErrorStackTrace = ex?.ToString(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds
        }, ct);
    }

    private Task WriteReportPayloadDiagnosticsRepairLogAsync(
        WorkReportPayloadDiagnosticsRepairResult result,
        DateTime startedAtUtc,
        string actorUserId,
        Exception? ex,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        var logResult = ex is not null
            ? "FAILED"
            : result.DryRun
                ? "DRY_RUN"
                : result.FailedCount == 0 ? "SUCCESS" : "PARTIAL_FAILED";

        return _operationLogs.WriteAsync(new WorkStatusOperationLog
        {
            Operation = "REPORT_PAYLOAD_DIAGNOSTICS_REPAIR",
            Scope = "report-payload-diagnostics",
            Result = logResult,
            WorkId = result.Diagnostics.WorkId,
            WorkAssignmentId = result.Diagnostics.WorkAssignmentId,
            WorkReportPeriodId = result.Diagnostics.WorkReportPeriodId,
            WorkAssignmentReportId = result.Diagnostics.WorkAssignmentReportId,
            ActorUserId = actorUserId,
            Summary = BuildReportPayloadDiagnosticsRepairSummary(result),
            ErrorType = ex?.GetType().FullName,
            ErrorMessage = ex?.Message,
            ErrorStackTrace = ex?.ToString(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds
        }, ct);
    }

    private static string BuildReportPayloadDiagnosticsRepairSummary(
        WorkReportPayloadDiagnosticsRepairResult result)
        => $"dryRun={result.DryRun};limit={result.Limit};issues={result.Diagnostics.IssueCount};plannedPayloadOrphans={result.PlannedOrphanPayloadRows};softDeletedPayloads={result.SoftDeletedPayloadRows};plannedTableOrphans={result.PlannedOrphanTableValueRows};softDeletedTableValues={result.SoftDeletedTableValueRows};plannedStatisticTemplates={result.PlannedStatisticTemplateRebuilds};enqueuedStatisticTemplates={result.EnqueuedStatisticTemplateRebuilds};failed={result.FailedCount}";

    private static string BuildAdvancedSummaryNodeCleanupSummary(
        AdvancedSummaryNodeCleanupRequest request,
        AdvancedSummaryNodeCleanupResponse? result)
        => $"dryRun={request.DryRun};limit={JobRunManagementService.NormalizeAdvancedSummaryCleanupLimit(request.Limit)};grain={request.Grain};status={request.Status};dynamicFormTemplateId={request.DynamicFormTemplateId};sectionId={request.SectionId};configId={request.ConfigId};configVersionNo={request.ConfigVersionNo};configHash={request.ConfigHash};sourceSignatureHash={request.SourceSignatureHash};updatedBeforeUtc={request.UpdatedBeforeUtc:O};builtBeforeUtc={request.BuiltBeforeUtc:O};matched={result?.MatchedCount ?? 0};selected={result?.SelectedCount ?? 0};softDeleted={result?.SoftDeletedCount ?? 0};hasMore={result?.HasMore ?? false}";
}
