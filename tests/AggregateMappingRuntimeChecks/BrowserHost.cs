using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Models;
using Microsoft.Extensions.Logging.Abstractions;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Opt-in UI host for synthetic fixtures only. Actual Program/auth/report/aggregate APIs;
// no bypass endpoint or production credentials. Optional jobs use this fixture's isolated prefix.
internal static class BrowserHost
{
    internal static async Task Run(MongoDbContext db, string run)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        var ct = lifetime.Token;
        var fixture = await RuntimeFixture.Seed(db, run, ct);
        var runner = new DynamicFlowDefinitionTransactionRunner(db, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance);
        var payload = new WorkReportPayloadService(db);
        var store = new AggregateMongoStore(db, runner, payload, payload);
        var previewJobs = Environment.GetEnvironmentVariable("P05_BROWSER_JOBS") == "1";
        await store.ExecuteAsync(async (tx, token) => {
            var declaration = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run + "-source", 1);
            await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + fixture.Source.Id, 0,
                new AggregateDeclarationState("REPORT", fixture.Source.Id, fixture.WorkId, fixture.Source.WorkAssignmentId, declaration),
                fixture.WorkId, fixture.Source.Id, [], token);
            return true;
        }, ct);
        await SeedMissingSlotDeclarations(db, fixture.WorkId, ct);
        if (previewJobs)
        {
            // Seed only the starting diagram; the browser still uses the real preview/Apply APIs.
            await store.ExecuteAsync(async (tx, token) => {
                var declaration = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run, 1);
                await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + fixture.Report.Id, 0,
                    new AggregateDeclarationState("REPORT", fixture.Report.Id, fixture.WorkId, fixture.Report.WorkAssignmentId, declaration), fixture.WorkId, fixture.Report.Id, [], token);
                return true;
            }, ct);
            var reader = new AggregateMongoCommandReader(db, payload, true);
            var context = (await new AggregateMongoPreviewReader(db, payload, new AggregateMongoDataWindows(db), true).BootstrapAsync(fixture.Report.Id, fixture.Actor, ct)).Context;
            var authority = await reader.AuthorizeAsync(context, fixture.Actor, run, ct);
            context = authority.Read.Context;
            AggregateFormPinDto Pin(DynamicFormTemplate f) => new(f.Id, f.FamilyId!, f.VersionNo, f.PublishedSchemaHash!);
            var recipe = new AggregateRecipeDto { SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1",
                Nodes = [new() { Id = "s", Kind = "SOURCE", Form = Pin(fixture.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "NUMBER", "SET", "n", "w")] },
                    new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "NUMBER", "SET")], Outputs = [new("out", "NUMBER", "SINGLE")], Expressions = [new("out", new() { Kind = "CALL", Name = "SUM", Arguments = [new() { Kind = "INPUT", Ref = "in" }] })] },
                    new() { Id = "t", Kind = "TARGET", Form = Pin(fixture.TargetForm), Inputs = [new("in", "NUMBER", "SINGLE", "total")], Outputs = [] }],
                Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "out"), new("t", "in"))],
                TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
            var commands = new AggregateCommandService(store, reader, new(RandomNumberGenerator.GetBytes(32)));
            var head = await commands.CreateConfigAsync(new(run + "-config", "CONFIG_CREATE", context.BindingId, fixture.Actor, run, DateTimeOffset.UtcNow), context,
                new(run + "-config", context.BindingId, Pin(fixture.TargetForm), recipe), ct);
            await commands.CreateInstanceAsync(new(run + "-instance", "INSTANCE_CREATE", fixture.Report.Id, fixture.Actor, run, DateTimeOffset.UtcNow), context, head.Id, ct);
        }
        foreach (var form in new[] { fixture.SourceForm, fixture.TargetForm })
            await db.WorkReportPeriods.UpdateManyAsync(p => p.WorkId == fixture.WorkId && p.DynamicFormTemplateId == form.Id,
                Builders<WorkReportPeriod>.Update.Set(p => p.DynamicFormTemplateName, form.Name).Set(p => p.DynamicFormTemplateCode, form.Code), cancellationToken: ct);
        var user = await db.Users.Find(u => u.Id == fixture.Actor).SingleAsync(ct);
        user.Username = (run + "-ui").ToLowerInvariant();
        user.FullName = "P05 kiểm giao diện tổng hợp";
        var password = Environment.GetEnvironmentVariable("P05_BROWSER_PASSWORD") ?? throw new InvalidOperationException("P05_BROWSER_PASSWORD required");
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        await db.Users.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: ct);
        await db.Units.InsertOneAsync(new Unit { Id = user.UnitId!, Code = run, FullName = "Đơn vị thử P05", ShortName = "P05", CreatedByUserId = user.Id }, cancellationToken: ct);
        var projection = new tdtd_be.Services.Common.DocRoleReadModelProjectionService(db);
        var roles = new tdtd_be.Services.Common.DocRoleService(db, projection);
        await roles.UpsertWorkRootRolesAsync(await db.Works.Find(w => w.Id == fixture.WorkId).SingleAsync(ct), ct);
        foreach (var assignment in await db.WorkAssignments.Find(a => a.WorkId == fixture.WorkId).ToListAsync(ct))
            await roles.UpsertWorkAssignmentRolesAsync(assignment, ct);
        await roles.RebuildWorkParticipantRolesFromAssignmentsAsync(fixture.WorkId, user.Id, ct);
        await projection.RebuildWorkReportPeriodsAsync(fixture.WorkId, user.Id, ct);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        foreach (var pair in new Dictionary<string, string> {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing", ["DOTNET_ENVIRONMENT"] = "Testing", ["ASPNETCORE_URLS"] = "http://127.0.0.1:5306",
            ["Mongo__ConnectionString"] = "mongodb://localhost:27017/?replicaSet=tdtd-rs", ["Mongo__Database"] = "tdtd", ["Mongo__TestingSkipIndexInitialization"] = "false",
            ["Hangfire__ServerEnabled"] = previewJobs ? "true" : "false", ["Hangfire__DashboardEnabled"] = "false", ["Hangfire__RecurringRegistrationEnabled"] = "false",
            ["Hangfire__Prefix"] = "p05_ui_" + run.Replace('-', '_'), ["Redis__Enabled"] = "false", ["Frontend__Enabled"] = "false",
            ["Cors__AllowedOrigins__0"] = "http://127.0.0.1:5305",
            ["Jwt__Issuer"] = run, ["Jwt__Audience"] = run, ["Jwt__Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["AggregateMapping__V2Enabled"] = "true", ["AggregateMapping__ConfirmationKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }) start.Environment[pair.Key] = pair.Value;
        start.Environment.Remove("P05_BROWSER_PASSWORD");
        var stop = Path.Combine(AppContext.BaseDirectory, run + "-browser.stop");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("P05 UI host startup failed");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Console.WriteLine($"BROWSER hostPid={process.Id} user={user.Username} work={fixture.WorkId} report={fixture.Report.Id} stop={stop}");
            while (!process.HasExited && !File.Exists(stop)) await Task.Delay(1000, ct);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, run + "-browser-host.log"), await stdout + await stderr);
            // Preserve the fixture and audit history, retire only the synthetic login we created.
            await db.Users.UpdateOneAsync(u => u.Id == user.Id, Builders<AppUser>.Update.Set(u => u.PasswordHash, "!P05_RETIRED!"));
            await db.RefreshTokens.UpdateManyAsync(t => t.UserId == user.Id && t.RevokedAt == null,
                Builders<RefreshTokenDoc>.Update.Set(t => t.RevokedAt, DateTime.UtcNow));
            Console.WriteLine("BROWSER stopped; synthetic password and refresh sessions retired; fixtures retained.");
        }
    }

    internal static async Task SeedMissingSlotDeclarations(MongoDbContext db, string workId, CancellationToken ct)
    {
        var work = await db.Works.Find(w => w.Id == workId && !w.IsDeleted).SingleAsync(ct);
        if (!work.AutoCode.StartsWith("p05-", StringComparison.Ordinal) || work.DynamicFlowRuntimeInstanceId != null)
            throw new InvalidOperationException("Only the explicitly named synthetic P05 fixture may be prepared");
        var payload = new WorkReportPayloadService(db);
        var store = new AggregateMongoStore(db, new DynamicFlowDefinitionTransactionRunner(db, NullLogger<DynamicFlowDefinitionTransactionRunner>.Instance), payload, payload);
        foreach (var report in await db.WorkAssignmentReports.Find(r => r.WorkId == workId && !r.IsDeleted).ToListAsync(ct))
        {
            var period = await db.WorkReportPeriods.Find(p => p.Id == report.WorkReportPeriodId).SingleAsync(ct);
            var slot = period.WorkTemplateAssigneeId + ":" + report.PeriodKey;
            await store.ExecuteAsync(async (tx, token) => {
                var declaration = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, "REPORT:" + report.Id, token);
                var existing = await tx.GetAsync<AggregateDeclarationState>(AggregateCollections.Declarations, "SLOT:" + slot, token);
                if (declaration != null && existing == null)
                    await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slot, 0,
                        new AggregateDeclarationState("SLOT", slot, workId, report.WorkAssignmentId, declaration.Value.Declaration), workId, slot, [], token);
                return true;
            }, ct);
        }
        Console.WriteLine("BROWSER fixture slot declarations prepared from its explicit report declarations: " + workId);
    }
}
