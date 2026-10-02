using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class FullHostSlotChecks
{
    internal static async Task Run(MongoDbContext db, RuntimeFixture f, string instanceId, string run,
        Func<string, Task> actor,
        Func<HttpMethod, string, object?, Task<(HttpStatusCode Status, JsonElement Body)>> send, CancellationToken ct)
    {
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS full-host-slot " + name); }
        async Task<JsonElement> Call(string path, object? body = null)
        {
            var result = await send(HttpMethod.Post, path, body);
            if (result.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                throw new InvalidOperationException($"Slot HTTP {path} {(int)result.Status}: {result.Body}");
            return result.Body;
        }
        async Task<AggregateInstanceState> State() => AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances)
            .Find(new BsonDocument("_id", instanceId)).SingleAsync(ct)).Value;
        async Task<string> Frozen() => string.Join('\n', (await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen)
            .Find(new BsonDocument("target", f.Report.Id)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
        var period = await db.WorkReportPeriods.Find(p => p.Id == f.Source.WorkReportPeriodId).SingleAsync(ct);
        var source = await db.WorkAssignmentReports.Find(r => r.Id == f.Source.Id).SingleAsync(ct);
        // Preserve the previous report as non-current history; the slot now needs a new current version.
        await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == source.Id,
            Builders<WorkAssignmentReport>.Update.Set(r => r.IsCurrent, false), cancellationToken: ct);
        await db.WorkReportPeriods.UpdateOneAsync(p => p.Id == period.Id, Builders<WorkReportPeriod>.Update
            .Set(p => p.CurrentReportId, null).Set(p => p.ReportVersionCount, source.VersionNo).Set(p => p.Status, WorkReportPeriodStatus.Pending), cancellationToken: ct);
        await actor(f.Actor);
        var boot = await Call("aggregate-v2/editor/bootstrap", new { reportId = f.Report.Id });
        var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var state = await State();
        var change = new AggregateMappingChangeDto(state.Revision, null, state.Selection, [], false);
        var review = await Call($"aggregate-v2/instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change));
        await Call($"aggregate-v2/instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + "-http-slot-apply", context, change, review.GetProperty("token").GetString()!));
        var confirmation = await Call($"aggregate-v2/reports/{f.Report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
        var target = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
        await Call($"work-assignment-reports/{target.Id}/submit", new SubmitWorkAssignmentReportRequest {
            CommandId = run + "-http-slot-submit", ExpectedPayloadRevision = target.PayloadRevision, ExpectedLifecycleRevision = target.LifecycleRevision,
            AggregateConfirmationToken = confirmation.GetProperty("token").GetString(), LateReason = "P05 fixture" });
        var slotId = "SLOT:" + period.WorkTemplateAssigneeId + ":" + period.PeriodKey;
        async Task<int> Owners() => AggregateMongoTransaction.Read<AggregateLockState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks)
            .Find(new BsonDocument("_id", slotId)).SingleAsync(ct)).Value.Owners.Count;
        Check((await State()).State == "FROZEN" && await Owners() == 1,
            "actual Submit with known missing optional input freezes and acquires missing-slot owner");
        var frozen = await Frozen();
        var beforeReports = await db.WorkAssignmentReports.CountDocumentsAsync(r => r.WorkId == f.WorkId, cancellationToken: ct);
        var beforePeriod = (await db.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync(ct)).ToJson();
        await actor(source.AssigneeUserId);
        var denied = await send(HttpMethod.Post, $"work-report-periods/{period.Id}/open", null);
        Check((int)denied.Status >= 400 && denied.Body.GetRawText().Contains("AGG_SOURCE_LOCKED")
            && beforeReports == await db.WorkAssignmentReports.CountDocumentsAsync(r => r.WorkId == f.WorkId, cancellationToken: ct)
            && beforePeriod == (await db.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync(ct)).ToJson()
            && frozen == await Frozen(), "actual OpenPeriod cannot create report or change period while the missing slot is owned");
        await actor(f.Outsider);
        target = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
        if (target.Status == WorkAssignmentReportStatus.Approved)
        {
            await Call($"work-assignment-review/reports/{target.Id}/recall-approved", new ReturnReportRequest {
                CommandId = run + "-http-slot-recall", ExpectedPayloadRevision = target.PayloadRevision, ExpectedLifecycleRevision = target.LifecycleRevision, Comment = "P05 fixture" });
            target = await db.WorkAssignmentReports.Find(r => r.Id == target.Id).SingleAsync(ct);
        }
        await Call($"work-assignment-review/reports/{target.Id}/return", new ReturnReportRequest {
            CommandId = run + "-http-slot-return", ExpectedPayloadRevision = target.PayloadRevision, ExpectedLifecycleRevision = target.LifecycleRevision, Comment = "P05 fixture" });
        Check(await Owners() == 0 && frozen == await Frozen(), "actual reviewer Return releases missing slot while frozen history stays unchanged");
        await actor(source.AssigneeUserId);
        await Call($"work-report-periods/{period.Id}/open");
        var openedPeriod = await db.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync(ct);
        var opened = await db.WorkAssignmentReports.Find(r => r.Id == openedPeriod.CurrentReportId).SingleAsync(ct);
        Check(opened.Id != source.Id && opened.IsCurrent && opened.Status == WorkAssignmentReportStatus.Draft && opened.VersionNo == source.VersionNo + 1,
            "actual OpenPeriod materializes a new current draft after final owner release");
        var declaration = AggregateMongoTransaction.Read<AggregateDeclarationState>(await db.Db.GetCollection<BsonDocument>(AggregateCollections.Declarations)
            .Find(new BsonDocument("_id", "REPORT:" + opened.Id)).SingleAsync(ct)).Value;
        Check(declaration.Declaration.StartDate == "2026-09-01" && declaration.Declaration.EndDate == "2026-09-30",
            "actual materialization carries explicit slot data window without using deadline");
        await Call($"work-report-periods/{period.Id}/open");
        Check((await db.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync(ct)).CurrentReportId == opened.Id
            && await db.WorkAssignmentReports.CountDocumentsAsync(r => r.WorkId == f.WorkId, cancellationToken: ct) == beforeReports + 1
            && frozen == await Frozen(), "reopening uses the same current report without duplicate creation or frozen-history changes");
    }
}
