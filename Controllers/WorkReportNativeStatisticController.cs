using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.StatisticsRun;
using System.Text.Json;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/work-report-native-statistics")]
public sealed class WorkReportNativeStatisticController(P9DirectResultService results, IStatRunCandidateActivation activation) : ControllerBase
{
    [HttpPost("result")]
    public async Task<ActionResult<NativeStatisticResultResponse>> Read([FromBody] JsonElement body, CancellationToken ct)
    {
        activation.RequireCapability(StatRunCapabilities.DirectFieldTableLabel, StatRunRouteRegistry.DirectTableResult);
        return Ok(await results.ReadNativeAsync(StatConfigCanonicalJson.DeserializeStrict<NativeStatisticResultRequest>(body), ct));
    }
}
