using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;
using tdtd_be.Services.WorkAssignments.SummaryTokens;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Controllers;

// Bản deploy gọn: tạm khóa toàn bộ API quản lý quota, giữ mã để mở lại.
[NonController]
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
    public Task<IActionResult> Grant(
        [FromBody] WorkSummaryTokenGrantRequest request,
        CancellationToken ct)
        => throw AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new
            {
                path = "$.operation",
                reason = "WORK_SUMMARY_TOKEN_LEGACY_GRANT_BLOCKED",
                replacement =
                    "POST /api/work-summary-tokens/pools/{ownerUnitId}/grants"
            });

    [HttpPost("pools/{ownerUnitId}/grants")]
    public async Task<IActionResult> GrantP8(
        [FromRoute] string ownerUnitId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var command = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkSummaryTokenGrantP8Payload>>(body);
        return Ok(await _tokens.GrantP8Async(
            ownerUnitId,
            command,
            _me.RequireMe(),
            ct));
    }

    [HttpPost("entries/{ledgerId}/compensations")]
    public async Task<IActionResult> CompensateP8(
        [FromRoute] string ledgerId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var command = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkSummaryTokenCompensationP8Payload>>(body);
        return Ok(await _tokens.CompensateP8Async(
            ledgerId,
            command,
            _me.RequireMe(),
            ct));
    }

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
