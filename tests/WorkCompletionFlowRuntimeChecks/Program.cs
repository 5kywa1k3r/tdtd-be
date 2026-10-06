using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

if (args.Length == 2 && await WorkReopenProbe.TryRunAsync(args[0], args[1])) return;
if (args.Length == 2 && await NoReportReopenChecks.TryRunAsync(args[0], args[1])) return;
if (args.Length == 2 && await CompletionLoadChecks.TryRunAsync(args[0], args[1])) return;
if (args.Length == 2 && await CloseoutAuditChecks.TryRunAsync(args[0], args[1])) return;

if (args.Length == 2 && args[0] == "--normalize")
{
    var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(args[1])).RootElement;
    var fixtureDatabase = manifest.GetProperty("database").GetString()!;
    if (!fixtureDatabase.StartsWith("p05_completion_flow_p05_flow_20261006_", StringComparison.Ordinal)) throw new InvalidOperationException("Own fixture only");
    var fixtureDb = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = fixtureDatabase }));
    foreach (var f in manifest.GetProperty("fixtures").EnumerateArray())
    {
        var workId = f.GetProperty("workId").GetString()!;
        var w = await fixtureDb.Works.Find(w => w.Id == workId).SingleAsync();
        if (!w.AutoCode.StartsWith(manifest.GetProperty("run").GetString()!, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture identity mismatch");
        var reviewer = await fixtureDb.Users.Find(u => u.Username == manifest.GetProperty("run").GetString() + (f.GetProperty("kind").GetString() == "ONCE" ? "-once-reviewer" : "-periodic-reviewer")).SingleAsync();
        await fixtureDb.Works.UpdateOneAsync(x => x.Id == w.Id && x.UpdatedAtUtc == w.UpdatedAtUtc,
            Builders<Work>.Update.Set(x => x.CreatedByUserId, reviewer.Id).Set(x => x.LeaderDirectiveUserId, reviewer.Id)
                .Set(x => x.StartDate, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))
                .Set(x => x.DueDate, new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc)).Set(x => x.EndDate, new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc)));
        var kind = f.GetProperty("kind").GetString() == "ONCE" ? "ONCE" : "PERIODIC_REPORT";
        await fixtureDb.WorkAssignments.UpdateManyAsync(a => a.WorkId == w.Id, Builders<WorkAssignment>.Update.Set(a => a.AssignmentType, kind)
            .Set(a => a.StartDate, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)).Set(a => a.DueDate, new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc)));
        await fixtureDb.WorkTemplateAssignees.UpdateManyAsync(b => b.WorkId == w.Id, Builders<WorkTemplateAssignee>.Update.Set(b => b.AssignmentType, kind));
        var projection = new DocRoleReadModelProjectionService(fixtureDb);
        var roles = new DocRoleService(fixtureDb, projection);
        await roles.UpsertWorkRootRolesAsync(await fixtureDb.Works.Find(x => x.Id == w.Id).SingleAsync(), default);
        foreach (var a in await fixtureDb.WorkAssignments.Find(x => x.WorkId == w.Id).ToListAsync()) await roles.UpsertWorkAssignmentRolesAsync(a, default);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(w.Id, reviewer.Id, default);
        await projection.RebuildWorkReportPeriodsAsync(w.Id, reviewer.Id, default);
    }
    await FixtureProvenanceChecks.Migration(fixtureDb, "after fixture scope normalization", default);
    Console.WriteLine("Normalized own fixtures; form/report pins and payloads unchanged.");
    return;
}
if (args.Length != 2 || args[0] != "--seed") throw new ArgumentException("--seed <manifest-path> required");
var password = Environment.GetEnvironmentVariable("P05_FLOW_FIXTURE_PASSWORD") ?? throw new InvalidOperationException("Fixture password required in process environment");
var run = "p05-flow-20261006-" + Guid.NewGuid().ToString("N")[..8];
var database = "p05_completion_flow_" + run.Replace('-', '_');
var db = new MongoDbContext(Options.Create(new MongoOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=tdtd-rs", Database = database }));
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = timeout.Token;
await FixtureProvenanceChecks.Migration(db, "before seed", ct);
var runner = new DynamicFlowDefinitionTransactionRunner(db, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
var payload = new WorkReportPayloadService(db);
var store = new AggregateMongoStore(db, runner, payload, payload);
var periodic = await PeriodicFixture.Seed(db, store, run, ct);
var once = await RuntimeFixture.Seed(db, run + "-once", ct);
var manifests = new List<object>();
foreach (var root in new[] { periodic.Root, once })
{
    var unitId = (await db.Users.Find(u => u.Id == root.Actor).SingleAsync(ct)).UnitId!;
    var unit = new Unit { Id = unitId, Code = root.WorkId, FullName = "Đơn vị kiểm thử P05", ShortName = "P05", CreatedByUserId = root.Actor };
    await db.Units.InsertOneAsync(unit, cancellationToken: ct);
    var roles = new[] { (Id: root.Actor, Role: "author", Name: "PV01 — người lập tổng hợp"),
        (Id: root.Outsider, Role: "reviewer", Name: "Giám đốc — người giao và duyệt"),
        (Id: root.Source.AssigneeUserId, Role: "source", Name: "PX01 — người báo cáo nguồn") };
    foreach (var role in roles)
    {
        var user = await db.Users.Find(u => u.Id == role.Id).SingleAsync(ct);
        user.Username = run + (root == once ? "-once-" : "-periodic-") + role.Role;
        user.FullName = role.Name;
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        await db.Users.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: ct);
    }
    var work = await db.Works.Find(w => w.Id == root.WorkId).SingleAsync(ct);
    work.Name = root == once ? "P05 — vòng báo cáo một lần" : "P05 — vòng báo cáo định kỳ";
    work.Owner = new UserRef { UserId = root.Outsider, FullName = roles[1].Name, UnitId = unitId };
    work.CreatedByUserId = root.Outsider; work.LeaderDirectiveUserId = root.Outsider;
    work.StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc); work.DueDate = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc); work.EndDate = work.DueDate;
    await db.Works.ReplaceOneAsync(w => w.Id == work.Id, work, cancellationToken: ct);
    foreach (var a in await db.WorkAssignments.Find(a => a.WorkId == work.Id).ToListAsync(ct))
    {
        var target = a.Id == root.Report.WorkAssignmentId;
        a.Name = target ? "PV01 tổng hợp báo cáo" : "PX01 báo cáo nguồn";
        a.CreatedByUserId = target ? root.Outsider : root.Actor; a.CurrentReviewerUserId = a.CreatedByUserId;
        a.StartDate = work.StartDate; a.DueDate = work.DueDate;
        a.AssignmentType = root == once ? "ONCE" : "PERIODIC_REPORT";
        var form = target ? root.TargetForm : root.SourceForm;
        a.DynamicFormTemplateName = form.Name; a.DynamicFormTemplateCode = form.Code;
        foreach (var u in a.Assignees) { u.FullName = roles.Single(r => r.Id == u.UserId).Name; u.UnitId = unitId; u.UnitName = unit.FullName; }
        await db.WorkAssignments.ReplaceOneAsync(x => x.Id == a.Id, a, cancellationToken: ct);
    }
    await db.WorkTemplateAssignees.UpdateManyAsync(b => b.WorkId == work.Id, Builders<WorkTemplateAssignee>.Update.Set(b => b.AssignmentType, root == once ? "ONCE" : "PERIODIC_REPORT"), cancellationToken: ct);
    foreach (var report in await db.WorkAssignmentReports.Find(r => r.WorkId == work.Id).ToListAsync(ct))
    {
        var form = report.DynamicFormTemplateId == root.TargetForm.Id ? root.TargetForm : root.SourceForm;
        await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == report.WorkReportPeriodId, Builders<WorkReportPeriod>.Update
            .Set(p => p.DynamicFormTemplateName, form.Name).Set(p => p.DynamicFormTemplateCode, form.Code)
            .Set(p => p.AssigneeUnitId, unitId).Set(p => p.ReportTitle, report.AssigneeUserId == root.Actor ? "Báo cáo tổng hợp PV01" : "Báo cáo nguồn PX01"), cancellationToken: ct);
    }
    var projection = new DocRoleReadModelProjectionService(db);
    var docRoles = new DocRoleService(db, projection);
    await docRoles.UpsertWorkRootRolesAsync(work, ct);
    foreach (var a in await db.WorkAssignments.Find(a => a.WorkId == work.Id).ToListAsync(ct)) await docRoles.UpsertWorkAssignmentRolesAsync(a, ct);
    await docRoles.RebuildWorkParticipantRolesFromAssignmentsAsync(work.Id, root.Actor, ct);
    await projection.RebuildWorkReportPeriodsAsync(work.Id, root.Actor, ct);
    manifests.Add(new { kind = root == once ? "ONCE" : "PERIODIC", workId = work.Id,
        assignmentId = root.Report.WorkAssignmentId, sourceAssignmentId = root.Source.WorkAssignmentId,
        users = roles.Select(r => new { r.Id, username = run + (root == once ? "-once-" : "-periodic-") + r.Role, fullName = r.Name }).ToArray(),
        reports = await db.WorkAssignmentReports.Find(r => r.WorkId == work.Id).Project(r => new { r.Id, r.WorkAssignmentId, r.WorkReportPeriodId, r.AssigneeUserId, r.PeriodKey }).ToListAsync(ct) });
}
await FixtureProvenanceChecks.Migration(db, "after final pins and payloads", ct);
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { run, database, fixtures = manifests }, new JsonSerializerOptions { WriteIndented = true }), ct);
Console.WriteLine("FIXTURE ready " + database + "; manifest " + args[1] + "; no acceptance DB changed.");
