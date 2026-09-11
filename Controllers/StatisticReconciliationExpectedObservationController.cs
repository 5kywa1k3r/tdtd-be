using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/works/{workId}/statistics/{scopeAssignmentId}/reconciliations")]
public sealed class StatisticReconciliationExpectedObservationController : ControllerBase
{
    private readonly IStatisticReconciliationRunService _service;
    private readonly MeAccessor _me;

    public StatisticReconciliationExpectedObservationController(
        IStatisticReconciliationRunService service,
        MeAccessor me)
    {
        _service = service;
        _me = me;
    }

    [HttpGet("{reconciliationId}/expected-summary")]
    public Task<StatisticReconciliationExpectedBusinessSummaryResponse> GetBusinessSummary(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        [FromQuery] string? generationId,
        CancellationToken ct)
        => _service.GetExpectedBusinessSummaryAsync(
            workId,
            scopeAssignmentId,
            reconciliationId,
            generationId,
            _me.RequireMe(),
            ct);

    [HttpGet("{reconciliationId}/expected-provenance")]
    public Task<StatisticReconciliationExpectedProvenancePageResponse> GetProvenance(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        [FromQuery] string? generationId,
        [FromQuery] string? cursor,
        [FromQuery] string? pageSize,
        CancellationToken ct)
        => _service.GetExpectedProvenanceAsync(
            workId,
            scopeAssignmentId,
            reconciliationId,
            generationId,
            cursor,
            pageSize,
            _me.RequireMe(),
            ct);
}
