using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Operations;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Runtime;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/internal/status-repair")]
public sealed class InternalStatusRepairController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly IWorkAssignmentStatusRepairService _repair;
    private readonly IDocRoleReadModelRepairService _docRoleRepair;
    private readonly IDocRoleReadModelDriftService _docRoleDrift;
    private readonly IDocRoleReadModelProjectionRetryJobService _projectionRetry;
    private readonly IWorkStatusOperationLogService _statusLog;

    public InternalStatusRepairController(
        IConfiguration cfg,
        IWorkAssignmentStatusRepairService repair,
        IDocRoleReadModelRepairService docRoleRepair,
        IDocRoleReadModelDriftService docRoleDrift,
        IDocRoleReadModelProjectionRetryJobService projectionRetry,
        IWorkStatusOperationLogService statusLog)
    {
        _cfg = cfg;
        _repair = repair;
        _docRoleRepair = docRoleRepair;
        _docRoleDrift = docRoleDrift;
        _projectionRetry = projectionRetry;
        _statusLog = statusLog;
    }

    [HttpPost("works/{workId}/rebuild")]
    public async Task<IActionResult> RebuildWorkTree(string workId, CancellationToken ct)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var startedAtUtc = DateTime.UtcNow;
        try
        {
            await _repair.RebuildWorkTreeAsync(workId, ct);
            await WriteInternalOperationLogAsync(
                "STATUS_REPAIR_WORK_TREE",
                "status-repair",
                "SUCCESS",
                startedAtUtc,
                workId: workId,
                summary: "manual=true",
                ex: null,
                ct: ct);

            return Ok(new { ok = true, workId });
        }
        catch (Exception ex)
        {
            await WriteInternalOperationLogAsync(
                "STATUS_REPAIR_WORK_TREE",
                "status-repair",
                "FAILED",
                startedAtUtc,
                workId: workId,
                summary: "manual=true",
                ex: ex,
                ct: ct);
            throw;
        }
    }

    [HttpPost("docroles/works/{workId}/repair")]
    public async Task<IActionResult> RepairWorkDocRoles(
        string workId,
        [FromQuery] bool? dryRun = null,
        [FromQuery] int? limit = null,
        [FromQuery] bool includeAssignments = false,
        [FromQuery] bool includePeriods = false,
        CancellationToken ct = default)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var startedAtUtc = DateTime.UtcNow;
        try
        {
            var result = await _docRoleRepair.RepairWorkAsync(
                workId,
                BuildDocRoleRepairOptions(dryRun, limit, includeAssignments, includePeriods),
                ct);

            await WriteInternalOperationLogAsync(
                "DOCROLE_REPAIR_WORK",
                "docrole-read-model-repair",
                result.FailedCount == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                startedAtUtc,
                workId: workId,
                summary: BuildRepairSummary(result),
                ex: null,
                ct: ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            await WriteInternalOperationLogAsync(
                "DOCROLE_REPAIR_WORK",
                "docrole-read-model-repair",
                "FAILED",
                startedAtUtc,
                workId: workId,
                summary: $"dryRun={dryRun ?? true};limit={limit ?? 100};includeAssignments={includeAssignments};includePeriods={includePeriods}",
                ex: ex,
                ct: ct);
            throw;
        }
    }

    [HttpPost("docroles/assignments/{assignmentId}/repair")]
    public async Task<IActionResult> RepairAssignmentDocRoles(
        string assignmentId,
        [FromQuery] bool? dryRun = null,
        [FromQuery] int? limit = null,
        [FromQuery] bool includePeriods = false,
        CancellationToken ct = default)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var startedAtUtc = DateTime.UtcNow;
        try
        {
            var result = await _docRoleRepair.RepairAssignmentAsync(
                assignmentId,
                BuildDocRoleRepairOptions(dryRun, limit, includeAssignments: false, includePeriods: includePeriods),
                ct);

            await WriteInternalOperationLogAsync(
                "DOCROLE_REPAIR_ASSIGNMENT",
                "docrole-read-model-repair",
                result.FailedCount == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                startedAtUtc,
                assignmentId: assignmentId,
                summary: BuildRepairSummary(result),
                ex: null,
                ct: ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            await WriteInternalOperationLogAsync(
                "DOCROLE_REPAIR_ASSIGNMENT",
                "docrole-read-model-repair",
                "FAILED",
                startedAtUtc,
                assignmentId: assignmentId,
                summary: $"dryRun={dryRun ?? true};limit={limit ?? 100};includePeriods={includePeriods}",
                ex: ex,
                ct: ct);
            throw;
        }
    }

    [HttpPost("docroles/periods/{workReportPeriodId}/repair")]
    public async Task<IActionResult> RepairPeriodDocRoles(
        string workReportPeriodId,
        [FromQuery] bool? dryRun = null,
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var startedAtUtc = DateTime.UtcNow;
        try
        {
            var result = await _docRoleRepair.RepairPeriodAsync(
                workReportPeriodId,
                BuildDocRoleRepairOptions(dryRun, limit, includeAssignments: false, includePeriods: false),
                ct);

            await WriteInternalOperationLogAsync(
                "DOCROLE_REPAIR_PERIOD",
                "docrole-read-model-repair",
                result.FailedCount == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                startedAtUtc,
                periodId: workReportPeriodId,
                summary: BuildRepairSummary(result),
                ex: null,
                ct: ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            await WriteInternalOperationLogAsync(
                "DOCROLE_REPAIR_PERIOD",
                "docrole-read-model-repair",
                "FAILED",
                startedAtUtc,
                periodId: workReportPeriodId,
                summary: $"dryRun={dryRun ?? true};limit={limit ?? 100}",
                ex: ex,
                ct: ct);
            throw;
        }
    }

    [HttpGet("docroles/drift")]
    public async Task<IActionResult> CheckDocRoleReadModelDrift(
        [FromQuery] string? workId = null,
        [FromQuery] string? assignmentId = null,
        [FromQuery] string? workReportPeriodId = null,
        [FromQuery] string? userId = null,
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var startedAtUtc = DateTime.UtcNow;
        try
        {
            var result = await _docRoleDrift.CheckAsync(
                new DocRoleReadModelDriftOptions
                {
                    WorkId = workId,
                    AssignmentId = assignmentId,
                    WorkReportPeriodId = workReportPeriodId,
                    UserId = userId,
                    Limit = limit ?? 100
                },
                ct);

            await WriteInternalOperationLogAsync(
                "DOCROLE_DRIFT_CHECK",
                "docrole-read-model-drift",
                result.HasIssues ? "ISSUES_FOUND" : "SUCCESS",
                startedAtUtc,
                workId: result.WorkId,
                assignmentId: result.AssignmentId,
                periodId: result.WorkReportPeriodId,
                summary: BuildDriftSummary(result),
                ex: null,
                ct: ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            await WriteInternalOperationLogAsync(
                "DOCROLE_DRIFT_CHECK",
                "docrole-read-model-drift",
                "FAILED",
                startedAtUtc,
                workId: workId,
                assignmentId: assignmentId,
                periodId: workReportPeriodId,
                summary: $"userId={userId};limit={limit ?? 100}",
                ex: ex,
                ct: ct);
            throw;
        }
    }

    [HttpGet("operation-logs")]
    public async Task<IActionResult> SearchOperationLogs(
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
        if (!IsInternalRequest())
            return Unauthorized();

        var logs = await _statusLog.SearchAsync(
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

        return Ok(logs);
    }

    [HttpPost("docroles/projection-retry/process")]
    public async Task<IActionResult> ProcessDocRoleProjectionRetries(
        [FromQuery] int maxJobs = 20,
        CancellationToken ct = default)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var processed = await _projectionRetry.ProcessPendingJobsAsync(maxJobs, ct);
        return Ok(new { ok = true, processed, maxJobs = Math.Clamp(maxJobs, 1, 200) });
    }

    [HttpGet("operation-logs/{id}")]
    public async Task<IActionResult> GetOperationLog(
        string id,
        [FromQuery] bool includeStackTrace = true,
        CancellationToken ct = default)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        var log = await _statusLog.GetByIdAsync(id, includeStackTrace, ct);
        return log is null ? NotFound() : Ok(log);
    }

    private bool IsInternalRequest()
    {
        var expected = _cfg["InternalApis:RepairKey"];
        var actual = Request.Headers["X-Internal-Key"].FirstOrDefault();

        return !string.IsNullOrWhiteSpace(expected) &&
               string.Equals(expected, actual, StringComparison.Ordinal);
    }

    private DocRoleReadModelRepairOptions BuildDocRoleRepairOptions(
        bool? dryRun,
        int? limit,
        bool includeAssignments,
        bool includePeriods)
    {
        var actorUserId = Request.Headers["X-Actor-User-Id"].FirstOrDefault();

        return new DocRoleReadModelRepairOptions
        {
            DryRun = dryRun ?? true,
            Limit = limit ?? 100,
            IncludeAssignments = includeAssignments,
            IncludePeriods = includePeriods,
            ByUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId
        };
    }

    private string GetInternalActorUserId()
    {
        var actorUserId = Request.Headers["X-Actor-User-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId;
    }

    private async Task WriteInternalOperationLogAsync(
        string operation,
        string scope,
        string result,
        DateTime startedAtUtc,
        string? workId = null,
        string? assignmentId = null,
        string? periodId = null,
        string? reportId = null,
        string? summary = null,
        Exception? ex = null,
        CancellationToken ct = default)
    {
        var completedAtUtc = DateTime.UtcNow;
        await _statusLog.WriteAsync(new WorkStatusOperationLog
        {
            Operation = operation,
            Scope = scope,
            Result = result,
            WorkId = workId,
            WorkAssignmentId = assignmentId,
            WorkReportPeriodId = periodId,
            WorkAssignmentReportId = reportId,
            ActorUserId = GetInternalActorUserId(),
            Summary = summary,
            ErrorType = ex?.GetType().Name,
            ErrorMessage = ex?.Message,
            ErrorStackTrace = ex?.ToString(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds
        }, ct);
    }

    private static string BuildRepairSummary(DocRoleReadModelRepairResult result)
        => $"dryRun={result.DryRun};sourceFound={result.SourceFound};limit={result.Limit};includeAssignments={result.IncludeAssignments};includePeriods={result.IncludePeriods};plannedWork={result.PlannedWorkCount};plannedAssignments={result.PlannedAssignmentCount};plannedPeriods={result.PlannedPeriodCount};rebuiltWork={result.RebuiltWorkCount};rebuiltAssignments={result.RebuiltAssignmentCount};rebuiltPeriods={result.RebuiltPeriodCount};failed={result.FailedCount};truncatedAssignments={result.TruncatedAssignments};truncatedPeriods={result.TruncatedPeriods}";

    private static string BuildDriftSummary(DocRoleReadModelDriftResult result)
        => $"hasIssues={result.HasIssues};totalIssueCount={result.TotalIssueCount};limit={result.Limit};userId={result.UserId};lists={string.Join(",", result.Lists.Select(x => $"{x.Name}:{x.IssueCount}/{x.ScannedRowCount}{(x.Truncated ? ":truncated" : string.Empty)}"))}";
}
