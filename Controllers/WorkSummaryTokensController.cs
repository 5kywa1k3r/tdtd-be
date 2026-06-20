using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;
using tdtd_be.Services.WorkAssignments.SummaryTokens;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-summary-tokens")]
public sealed class WorkSummaryTokensController : ControllerBase
{
    private readonly IWorkSummaryTokenService _tokens;
    private readonly MeAccessor _me;

    public WorkSummaryTokensController(
        IWorkSummaryTokenService tokens,
        MeAccessor me)
    {
        _tokens = tokens;
        _me = me;
    }

    [HttpPost("grants")]
    public async Task<IActionResult> Grant(
        [FromBody] WorkSummaryTokenGrantRequest request,
        CancellationToken ct)
        => Ok(await _tokens.GrantAsync(request ?? new WorkSummaryTokenGrantRequest(), _me.RequireMe(), ct));

    [HttpGet("quota")]
    public async Task<IActionResult> GetQuota(
        [FromQuery] string? ownerUnitId = null,
        [FromQuery] string? tokenKind = null,
        [FromQuery] string? periodMonthKey = null,
        CancellationToken ct = default)
        => Ok(await _tokens.GetQuotaAsync(ownerUnitId ?? string.Empty, tokenKind, periodMonthKey, _me.RequireMe(), ct));

    [HttpGet("ledger")]
    public async Task<IActionResult> SearchLedger(
        [FromQuery] string? ownerUnitId = null,
        [FromQuery] string? ownerUserId = null,
        [FromQuery] string? actorUserId = null,
        [FromQuery] string? issuerUserId = null,
        [FromQuery] string? tokenKind = null,
        [FromQuery] string? direction = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? periodMonthKey = null,
        [FromQuery] string? configId = null,
        [FromQuery] string? jobId = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await _tokens.SearchLedgerAsync(new WorkSummaryTokenLedgerSearchRequest
        {
            OwnerUnitId = ownerUnitId,
            OwnerUserId = ownerUserId,
            ActorUserId = actorUserId,
            IssuerUserId = issuerUserId,
            TokenKind = tokenKind,
            Direction = direction,
            Outcome = outcome,
            PeriodMonthKey = periodMonthKey,
            ConfigId = configId,
            JobId = jobId,
            Query = q,
            Page = page,
            PageSize = pageSize
        }, _me.RequireMe(), ct));
}
