using Microsoft.AspNetCore.Mvc;
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

    public InternalStatusRepairController(
        IConfiguration cfg,
        IWorkAssignmentStatusRepairService repair,
        IDocRoleReadModelRepairService docRoleRepair)
    {
        _cfg = cfg;
        _repair = repair;
        _docRoleRepair = docRoleRepair;
    }

    [HttpPost("works/{workId}/rebuild")]
    public async Task<IActionResult> RebuildWorkTree(string workId, CancellationToken ct)
    {
        if (!IsInternalRequest())
            return Unauthorized();

        await _repair.RebuildWorkTreeAsync(workId, ct);
        return Ok(new { ok = true, workId });
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

        var result = await _docRoleRepair.RepairWorkAsync(
            workId,
            BuildDocRoleRepairOptions(dryRun, limit, includeAssignments, includePeriods),
            ct);

        return Ok(result);
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

        var result = await _docRoleRepair.RepairAssignmentAsync(
            assignmentId,
            BuildDocRoleRepairOptions(dryRun, limit, includeAssignments: false, includePeriods: includePeriods),
            ct);

        return Ok(result);
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

        var result = await _docRoleRepair.RepairPeriodAsync(
            workReportPeriodId,
            BuildDocRoleRepairOptions(dryRun, limit, includeAssignments: false, includePeriods: false),
            ct);

        return Ok(result);
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
}
