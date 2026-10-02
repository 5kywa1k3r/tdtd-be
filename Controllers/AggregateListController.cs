using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/aggregate-v2/lists"), RequestSizeLimit(16384)]
public sealed class AggregateListController(MongoDbContext db, IWorkReportPayloadReader payloads, IConfiguration config) : ControllerBase
{
    [HttpPost("page")]
    public Task<IActionResult> Page(AggregateListPageRequestDto request, CancellationToken ct) => Read(request, "PAGE", ct);
    [HttpPost("detail")]
    public Task<IActionResult> Detail(AggregateListPageRequestDto request, CancellationToken ct) => Read(request, "DETAIL", ct);
    [HttpPost("counts")]
    public Task<IActionResult> Counts(AggregateListPageRequestDto request, CancellationToken ct) => Read(request, "COUNTS", ct);
    private async Task<IActionResult> Read(AggregateListPageRequestDto request, string mode, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!AggregateIntegrationGate.V2Ready(config)) return Conflict(new { code = "AGG_ACTIVATION_REQUIRED" });
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var session = User.FindFirstValue("sid") ?? User.FindFirstValue("jti");
        if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(session)) return Unauthorized();
        try
        {
            var jobs = new AggregatePreviewJobs(db, payloads, config);
            if (string.IsNullOrEmpty(request.ReadId) == string.IsNullOrEmpty(request.JobId))
                throw new AggregatePreviewException("AGG_LIST_READ_SCOPE_INVALID");
            Task<System.Text.Json.JsonElement> AuthorizeRead() => !string.IsNullOrEmpty(request.ReadId)
                ? new AggregateListReadScopes(db, payloads).Authorize(request, actor, session, ct)
                : jobs.AuthorizeList(request.JobId!, request.TargetId, request.SnapshotId, request.SnapshotHash, actor, session, ct, request.IncludeLineage);
            var authorized = await AuthorizeRead();
            var store = new AggregateListStore(db);
            var result = mode == "COUNTS" ? ReportCounts(authorized, request) : mode == "DETAIL" ? await store.Detail(request.TargetId, request.SnapshotId, request.SnapshotHash,
                request.RowKey ?? "", request.FieldId ?? "", ct)
                : await store.Page(request.TargetId, request.SnapshotId, request.SnapshotHash, request.Offset, request.Limit, ct);
            var response = System.Text.Json.JsonSerializer.SerializeToNode(result, AggregateCanonical.Json)!;
            await new AggregateListDisplayMetadata(db).Enrich(response, mode, request.IncludeLineage, ct);
            await AuthorizeRead();
            if (!request.IncludeLineage)
            {
                if (mode == "DETAIL") { response.AsObject().Remove("origin"); response["cell"]?.AsObject().Remove("lineage"); }
                if (mode == "COUNTS") foreach (var item in response["items"]!.AsArray())
                { item!.AsObject().Remove("reportId"); item.AsObject().Remove("sourceSlot"); }
            }
            return Ok(response);
        }
        catch (AggregatePreviewException ex)
        { return StatusCode(ex.Code.Contains("FORBIDDEN") ? 403 : ex.Code.Contains("UNAVAILABLE") ? 404 : 409, new { code = ex.Code }); }
    }
    private static object ReportCounts(System.Text.Json.JsonElement root, AggregateListPageRequestDto request)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 50 || request.OperationIndex < 0) throw new AggregatePreviewException("AGG_PAGE_SIZE_INVALID");
        System.Text.Json.JsonElement? Find(System.Text.Json.JsonElement node)
        {
            if (node.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (node.TryGetProperty("listOperations", out var operations) && request.OperationIndex < operations.GetArrayLength())
                {
                    var operation = operations[request.OperationIndex];
                    if (operation.TryGetProperty("selection", out var selection) && selection.ValueKind == System.Text.Json.JsonValueKind.Object
                        && selection.GetProperty("id").GetString() == request.SnapshotId && selection.GetProperty("hash").GetString() == request.SnapshotHash
                        && operation.TryGetProperty("reports", out var reports)) return reports;
                }
                foreach (var p in node.EnumerateObject()) if (Find(p.Value) is { } found) return found;
            }
            else if (node.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) if (Find(item) is { } found) return found;
            return null;
        }
        var counts = Find(root) ?? throw new AggregatePreviewException("AGG_LIST_SNAPSHOT_UNAVAILABLE");
        var total = counts.GetArrayLength();
        if (request.Offset > total) throw new AggregatePreviewException("AGG_CURSOR_STALE");
        var items = counts.EnumerateArray().Skip(request.Offset).Take(request.Limit).Select(v => v.Clone()).ToArray();
        return new { Total = total, request.Offset, NextOffset = request.Offset + items.Length < total ? (int?)(request.Offset + items.Length) : null, Items = items };
    }
}
