using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class CalendarAccessChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, string run,
        AggregateRecipeDto prototype, string foreignReportId, Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct)
    {
        var payload = new WorkReportPayloadService(db); var store = new AggregateMongoStore(db, runner, payload, payload);
        var f = await PeriodicFixture.Seed(db, store, run + "-calendar", ct);
        var source = f.Sources[0]; var actor = f.Root.Actor;
        var period = await db.WorkReportPeriods.Find(p => p.Id == source.WorkReportPeriodId).SingleAsync(ct);
        // Fixture preparation: one weekly occurrence 28/09–04/10 under a monthly target.
        foreach (var extra in f.Sources.Skip(1))
        {
            await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == extra.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.IsDeleted, true), cancellationToken: ct);
            await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == extra.WorkReportPeriodId, Builders<WorkReportPeriod>.Update.Set(p => p.IsDeleted, true), cancellationToken: ct);
        }
        var start = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc); var end = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        await db.WorkTemplateAssignees.UpdateOneAsync(b => b.Id == period.WorkTemplateAssigneeId,
            Builders<WorkTemplateAssignee>.Update.Set(b => b.Schedule, new AssignmentSchedule { CycleType = "WEEKLY", StartDate = start, WeekDays = [7] })
                .Set(b => b.StartDate, start).Set(b => b.DueDate, end), cancellationToken: ct);
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == source.Id, Builders<WorkAssignmentReport>.Update
            .Set(r => r.PeriodKey, "20261004").Set(r => r.PeriodInstanceKey, period.WorkTemplateAssigneeId + ":20261004").Set(r => r.DueAtUtc, end), cancellationToken: ct);
        await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == period.Id, Builders<WorkReportPeriod>.Update
            .Set(p => p.PeriodKey, "20261004").Set(p => p.PeriodInstanceKey, period.WorkTemplateAssigneeId + ":20261004").Set(p => p.DueAtUtc, end), cancellationToken: ct);
        var declaration = new AggregateDataWindowDeclarationDto("2026-09-28", "2026-10-04", "USER_DECLARED", run, 2);
        await store.ExecuteAsync(async (tx, token) => {
            await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + source.Id, 1,
                new AggregateDeclarationState("REPORT", source.Id, f.Root.WorkId, source.WorkAssignmentId, declaration), f.Root.WorkId, source.Id, [], token);
            var slot = period.WorkTemplateAssigneeId + ":20261004";
            await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slot, 0,
                new AggregateDeclarationState("SLOT", slot, f.Root.WorkId, source.WorkAssignmentId, declaration with { Revision = 1 }), f.Root.WorkId, slot, [], token); return true;
        }, ct);
        AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
        var recipe = prototype with { Nodes = prototype.Nodes.Select(n => n.Kind == "SOURCE" ? n with { Form = Pin(f.Root.SourceForm) }
            : n.Kind == "TARGET" ? n with { Form = Pin(f.Root.TargetForm) } : n).ToList() };
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS calendar-acl " + name); }
        Task<JsonElement> Call(string path, object body, HttpStatusCode status = HttpStatusCode.OK, string? asActor = null)
            => post("aggregate-v2/" + path, body, asActor ?? actor, status);
        async Task<JsonElement> Preview(int month, AggregateTimeRuleDto rule, List<string>? explicitIds = null, HttpStatusCode status = HttpStatusCode.OK)
        {
            var boot = await Call("editor/bootstrap", new { reportId = f.Targets[month].Id });
            var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            var revisions = boot.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
            return await Call("instances/p05-calendar/preview", new AggregatePreviewRequestDto(context, "p05-calendar", "p05-unsaved", revisions,
                recipe with { TimeRules = [rule] }, new([new("s", explicitIds == null ? "FORM_SELECTOR" : "EXPLICIT_REPORTS", explicitIds ?? [], [])])), status);
        }
        var contained = recipe.TimeRules.Single(); var overlap = contained with { Match = "OVERLAPS_WHOLE_REPORT" };
        var preview = await Preview(0, contained);
        Check(preview.GetProperty("preview").GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT", "weekly 28 Sep–4 Oct is excluded by contained September window");
        preview = await Preview(0, overlap);
        Check(preview.GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10", "September whole-overlap includes entire weekly report without proration");
        preview = await Preview(1, overlap);
        Check(preview.GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10", "October whole-overlap may reuse same week only under explicit overlap choice");
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == source.Id, Builders<WorkAssignmentReport>.Update
            .Set(r => r.IsHistoricalData, true).Set(r => r.CompletedDate, new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc))
            .Set(r => r.SubmittedAtUtc, new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc)), cancellationToken: ct);
        var january = overlap with { ReportFilter = new("2026-01-15", "2026-01-15", "2026-10-04", "2026-10-04") };
        preview = await Preview(0, january);
        Check(preview.GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10", "historical report uses civil CompletedDate while deadline remains a separate filter");
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == source.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.IsHistoricalData, false), cancellationToken: ct);
        var october1 = overlap with { ReportFilter = new("2026-10-01", "2026-10-01") };
        preview = await Preview(0, october1);
        Check(preview.GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10", "current report uses SubmittedAtUtc converted to Vietnam day across midnight");
        preview = await Preview(0, january);
        Check(preview.GetProperty("preview").GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT", "current report ignores historical completion date for effective-date filtering");
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == source.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.SubmittedAtUtc, null), cancellationToken: ct);
        var denied = await Preview(0, october1, status: HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_METADATA_DATE_UNAVAILABLE", "missing current submission date fails closed rather than substituting deadline");
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == source.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.IsHistoricalData, true).Set(r => r.CompletedDate, null), cancellationToken: ct);
        denied = await Preview(0, january, status: HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_METADATA_DATE_UNAVAILABLE", "missing historical completion date is unavailable rather than zero");
        // Ownership of both Form definitions must not grant report/source access.
        await db.DynamicFormTemplates.UpdateManyAsync(t => t.Id == f.Root.TargetForm.Id || t.Id == f.Root.SourceForm.Id,
            Builders<DynamicFormTemplate>.Update.Set(t => t.CreatedByUserId, f.Root.Outsider), cancellationToken: ct);
        denied = await Call("editor/bootstrap", new { reportId = f.Targets[0].Id }, HttpStatusCode.Conflict, f.Root.Outsider);
        Check(denied.GetProperty("code").GetString() == "AGG_CONTEXT_UNAVAILABLE", "owning source and target Forms does not grant report context access");
        denied = await Preview(0, overlap, [foreignReportId], HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_SOURCE_UNAVAILABLE" && !denied.GetRawText().Contains(foreignReportId), "explicit source from another Work is rejected without report lineage leak");
        var originalParent = f.Root.Binding.WorkAssignmentId;
        await db.WorkAssignments.UpdateOneAsync(a => a.Id == source.WorkAssignmentId, Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, null), cancellationToken: ct);
        denied = await Preview(0, overlap, status: HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_SOURCE_UNAVAILABLE", "moving source out of direct-child scope revokes access despite Form ownership");
        await db.WorkAssignments.UpdateOneAsync(a => a.Id == source.WorkAssignmentId, Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, originalParent).Set(a => a.FlowInstanceId, ObjectId.GenerateNewId().ToString()), cancellationToken: ct);
        denied = await Preview(0, overlap, status: HttpStatusCode.NotFound);
        Check(denied.GetProperty("code").GetString() == "AGG_SOURCE_UNAVAILABLE", "Flow source is not exposed through Aggregate v2 lane");
        await db.WorkAssignments.UpdateOneAsync(a => a.Id == source.WorkAssignmentId, Builders<WorkAssignment>.Update.Set(a => a.FlowInstanceId, null), cancellationToken: ct);
    }
}
