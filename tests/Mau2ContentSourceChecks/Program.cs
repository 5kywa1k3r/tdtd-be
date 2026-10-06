// Read-only contract probe using the existing Mẫu 2 snapshot; no fixture/seed/write.
// Report authorization is a spy: these checks prove controller sequencing, not JWT policy.
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

if (!args.SequenceEqual(new[] { "--read-only-mau2" })) throw new ArgumentException("Requires --read-only-mau2");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = timeout.Token;
const string targetId = "6ac53e63c4378171baa7b346", sourceId = "6ac543d8c4378171baa7c549";
const string rowKey = "B2E722AE6925607CAA1BA27C86168B60A98DC5991723FEDEE27F82AB3832FD05";
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs&serverSelectionTimeoutMS=5000", Database = "tdtd" }));
var target = await db.WorkAssignmentReports.Find(r => r.Id == targetId).SingleAsync(ct);
var source = await db.WorkAssignmentReports.Find(r => r.Id == sourceId).SingleAsync(ct);
var payloads = new WorkReportPayloadService(db);
var payload = await payloads.LoadReportPayloadAsync(target, ct);
using var doc = JsonDocument.Parse(payload.TableValuesJson!);
var table = doc.RootElement.GetProperty("nativeTables").GetProperty("tables").EnumerateArray().Single(t => t.TryGetProperty("contentRef", out _));
var request = new AggregateContentReadRequest(targetId, table.GetProperty("tableId").GetString(), target.PayloadRevision,
    null, table.GetProperty("contentRef").Clone(), RowKey: rowKey);
var reports = DispatchProxy.Create<IWorkAssignmentReportService, ReportAccessSpy>();
var spy = (ReportAccessSpy)(object)reports;
spy.Reports = new Dictionary<string, WorkAssignmentReport> { [targetId] = target, [sourceId] = source };
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AggregateMapping:V2Enabled"] = "true" }).Build();
var controller = new AggregateContentController(db, payloads, reports, config);
void Actor(bool session = true) => controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
    User = new ClaimsPrincipal(new ClaimsIdentity(session ? new[] { new Claim(ClaimTypes.NameIdentifier, "reader"), new Claim("sid", "session") } : new[] { new Claim(ClaimTypes.NameIdentifier, "reader") }, "test")) } };
var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
Actor();
var ok = await controller.Source(request, ct) as OkObjectResult;
var opened = JsonSerializer.SerializeToElement(ok?.Value);
Check(opened.GetProperty("reportId").GetString() == sourceId && opened.GetProperty("workId").GetString() == target.WorkId, "sealed row resolves M01 report, never period id");
Check(spy.Calls.SequenceEqual(new[] { targetId, sourceId }), "target and source independently authorized in order");
Check(controller.Response.Headers.CacheControl == "no-store", "source identity response is not cached");
spy.Calls.Clear();
Check(await controller.Source(request with { PayloadRevision = target.PayloadRevision - 1 }, ct) is ObjectResult { StatusCode: 409 }, "stale target revision rejected");
Check(spy.Calls.SequenceEqual(new[] { targetId }), "stale request never reads source authority");
spy.Calls.Clear();
Check(await controller.Source(request with { RowKey = new string('0',64) }, ct) is ObjectResult { StatusCode: 404 }, "guessed row outside sealed manifest rejected");
Check(spy.Calls.SequenceEqual(new[] { targetId }), "nonmember row never reaches source authorization");
Check(await controller.Source(request with { TableId = "another-table" }, ct) is ObjectResult { StatusCode: 404 }, "reference cannot be borrowed from another table");
spy.Denied = sourceId;
Check(await controller.Source(request, ct) is ObjectResult { StatusCode: 403 }, "source access rejection is propagated without opening report");
spy.Denied = targetId; spy.Calls.Clear();
Check(await controller.Source(request, ct) is ObjectResult { StatusCode: 403 } && spy.Calls.SequenceEqual(new[] { targetId }), "target access rejection stops before source");
spy.Denied = null; spy.Calls.Clear(); Actor(false);
Check(await controller.Source(request, ct) is UnauthorizedResult && spy.Calls.Count == 0, "session required before any report read");
var targetAfter = await db.WorkAssignmentReports.Find(r => r.Id == targetId).SingleAsync(ct);
var sourceAfter = await db.WorkAssignmentReports.Find(r => r.Id == sourceId).SingleAsync(ct);
Check(targetAfter.PayloadRevision == target.PayloadRevision && targetAfter.LifecycleRevision == target.LifecycleRevision
    && sourceAfter.PayloadRevision == source.PayloadRevision && sourceAfter.LifecycleRevision == source.LifecycleRevision, "probe leaves both report revisions unchanged");
Console.WriteLine($"{checks} read-only contract checks passed; authorization spy, no HTTP/JWT claim.");

public class ReportAccessSpy : DispatchProxy
{
    public Dictionary<string, WorkAssignmentReport> Reports = new();
    public List<string> Calls = new();
    public string? Denied;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != nameof(IWorkAssignmentReportService.AuthorizeContentReadAsync)) throw new Exception("Unexpected service call: " + method?.Name);
        var id = (string)args![0]!; Calls.Add(id);
        return id == Denied ? Task.FromException<WorkAssignmentReport>(new AggregatePreviewException("AGG_SOURCE_FORBIDDEN")) : Task.FromResult(Reports[id]);
    }
}
