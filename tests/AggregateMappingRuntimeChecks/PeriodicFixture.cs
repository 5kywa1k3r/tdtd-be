using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal sealed record PeriodicFixture(RuntimeFixture Root, IReadOnlyList<WorkAssignmentReport> Targets, IReadOnlyList<WorkAssignmentReport> Sources)
{
    internal static async Task<PeriodicFixture> Seed(MongoDbContext db, AggregateMongoStore store, string run, CancellationToken ct)
    {
        var root = await RuntimeFixture.Seed(db, run + "-periodic", ct);
        var bindings = await db.WorkTemplateAssignees.Find(b => b.WorkId == root.WorkId).ToListAsync(ct);
        var schedule = new AssignmentSchedule { CycleType = "MONTHLY", StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), MonthDays = [30] };
        foreach (var b in bindings)
        {
            b.AssignmentType = "PERIODIC"; b.Schedule = schedule;
            b.DueDate = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);
            await db.WorkTemplateAssignees.ReplaceOneAsync(x => x.Id == b.Id, b, cancellationToken: ct);
        }
        await db.WorkAssignments.UpdateManyAsync(a => a.WorkId == root.WorkId,
            Builders<WorkAssignment>.Update.Set(a => a.AssignmentType, "PERIODIC").Set(a => a.Schedule, schedule), cancellationToken: ct);
        var targetBinding = bindings.Single(b => b.Id == root.Binding.Id);
        var sourceBinding = bindings.Single(b => b.Id != root.Binding.Id);
        var targets = new List<WorkAssignmentReport>(); var sources = new List<WorkAssignmentReport>();
        for (var month = 9; month <= 11; month++)
        {
            targets.Add(await Materialize(root.Report, targetBinding, month, "{}"));
            sources.Add(await Materialize(root.Source, sourceBinding, month, "{\"n\":" + (month == 9 ? 10 : month == 10 ? 20 : 40) + "}"));
        }
        Console.WriteLine($"PERIODIC work={root.WorkId} targets={string.Join(',', targets.Select(r => r.Id))} sources={string.Join(',', sources.Select(r => r.Id))}");
        return new(root, targets, sources);

        async Task<WorkAssignmentReport> Materialize(WorkAssignmentReport seed, WorkTemplateAssignee binding, int month, string fields)
        {
            var report = BsonSerializer.Deserialize<WorkAssignmentReport>(seed.ToBson());
            if (month != 9) { report.Id = ObjectId.GenerateNewId().ToString(); report.WorkReportPeriodId = ObjectId.GenerateNewId().ToString(); report.PayloadRevision = 0; }
            report.PeriodKey = $"2026{month:00}30"; report.PeriodInstanceKey = binding.Id + ":" + report.PeriodKey;
            // Due/schedule identity deliberately differs from October's declared data end (31).
            report.DueAtUtc = new DateTime(2026, month, 30, 0, 0, 0, DateTimeKind.Utc);
            var saved = await new WorkReportPayloadService(db).SaveReportPayloadAsync(report, "[]", fields, null, null, root.Actor, DateTime.UtcNow, ct);
            report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash; report.PayloadSizeBytes = saved.PayloadSizeBytes; report.PayloadStatus = saved.PayloadStatus;
            await db.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == report.Id, report, new ReplaceOptions { IsUpsert = true }, ct);
            await db.WorkReportPeriods.ReplaceOneAsync(p => p.Id == report.WorkReportPeriodId, new WorkReportPeriod {
                Id = report.WorkReportPeriodId, WorkId = root.WorkId, WorkAssignmentId = binding.WorkAssignmentId, WorkTemplateAssigneeId = binding.Id,
                CurrentReportId = report.Id, AssigneeUserId = binding.AssigneeUserId, PeriodKey = report.PeriodKey, PeriodInstanceKey = report.PeriodInstanceKey,
                DynamicFormTemplateId = report.DynamicFormTemplateId, DynamicFormFamilyId = report.DynamicFormFamilyId,
                DynamicFormVersionNo = report.DynamicFormVersionNo, DynamicFormSchemaHash = report.DynamicFormSchemaHash, DueAtUtc = report.DueAtUtc,
                CreatedByUserId = root.Actor }, new ReplaceOptions { IsUpsert = true }, ct);
            var declaration = new AggregateDataWindowDeclarationDto($"2026-{month:00}-01", $"2026-{month:00}-{DateTime.DaysInMonth(2026, month):00}", "USER_DECLARED", run, 1);
            await store.ExecuteAsync(async (tx, token) => {
                var slot = binding.Id + ":" + report.PeriodKey;
                await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + report.Id, 0,
                    new AggregateDeclarationState("REPORT", report.Id, root.WorkId, binding.WorkAssignmentId, declaration), root.WorkId, report.Id, [], token);
                await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slot, 0,
                    new AggregateDeclarationState("SLOT", slot, root.WorkId, binding.WorkAssignmentId, declaration), root.WorkId, slot, [], token);
                return true;
            }, ct);
            return report;
        }
    }
}
