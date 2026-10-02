using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class LifecycleChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, IConfiguration config,
        RuntimeFixture fixture, string run, string instanceId, string confirmation, CancellationToken ct, Func<Task>? onFrozen = null)
    {
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
        // Exercise the real host participant and native status in ONE transaction. The fixture
        // supplies the caller's validated transition; this does not claim full Submit/Return API policy coverage.
        async Task Transition(string operation, WorkAssignmentReportStatus status, string? token = null)
        {
            await runner.ExecuteAsync(async (session, cancellation) => {
                var before = await db.WorkAssignmentReports.Find(session, r => r.Id == fixture.Report.Id).SingleAsync(cancellation);
                await AggregateHostIntegration.LifecycleAsync(db, session, runner, before, operation, fixture.Actor, run + "-" + operation,
                    status, before.LifecycleRevision + 1, cancellation, config, run + "-session", token);
                await db.WorkAssignmentReports.UpdateOneAsync(session, r => r.Id == before.Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.Status, status).Inc(r => r.LifecycleRevision, 1), cancellationToken: cancellation);
                return true;
            }, ct);
        }
        await Transition("SUBMIT", WorkAssignmentReportStatus.Submitted, confirmation);
        var instances = db.Db.GetCollection<BsonDocument>(AggregateCollections.Instances);
        var frozen = db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen);
        var locks = db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks);
        var state = AggregateMongoTransaction.Read<AggregateInstanceState>(await instances.Find(new BsonDocument("_id", instanceId)).SingleAsync(ct)).Value;
        var snapshot = await frozen.Find(new BsonDocument("target", fixture.Report.Id)).SingleAsync(ct);
        var lockRow = await locks.Find(new BsonDocument("_id", "REPORT:" + fixture.Source.Id)).SingleAsync(ct);
        Check(state.State == "FROZEN" && AggregateMongoTransaction.Read<AggregateLockState>(lockRow).Value.Owners.Count == 1,
            "native lifecycle seam atomically submits, freezes mapping and acquires source owner");
        if (onFrozen != null) await onFrozen();
        var sourceBefore = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Source.Id).SingleAsync(ct);
        try
        {
            await runner.ExecuteAsync(async (session, cancellation) => {
                await AggregateHostIntegration.GuardReportAsync(db, session, sourceBefore, cancellation);
                await db.WorkAssignmentReports.UpdateOneAsync(session, r => r.Id == sourceBefore.Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.ReportTitle, "SHOULD_NOT_COMMIT"), cancellationToken: cancellation);
                return true;
            }, ct);
            throw new InvalidOperationException("locked source accepted mutation");
        }
        catch (AggregatePreviewException ex) when (ex.Code == "AGG_SOURCE_LOCKED") { }
        Check((await db.WorkAssignmentReports.Find(r => r.Id == sourceBefore.Id).SingleAsync(ct)).ReportTitle == sourceBefore.ReportTitle,
            "native source guard blocks edits while consumed by submitted report");
        await Transition("APPROVE", WorkAssignmentReportStatus.Approved);
        Check(snapshot.ToJson() == (await frozen.Find(new BsonDocument("target", fixture.Report.Id)).SingleAsync(ct)).ToJson(),
            "approval retains identical frozen snapshot");
        await Transition("RETURN", WorkAssignmentReportStatus.Draft);
        state = AggregateMongoTransaction.Read<AggregateInstanceState>(await instances.Find(new BsonDocument("_id", instanceId)).SingleAsync(ct)).Value;
        lockRow = await locks.Find(new BsonDocument("_id", "REPORT:" + fixture.Source.Id)).SingleAsync(ct);
        Check(state.State == "DRAFT" && AggregateMongoTransaction.Read<AggregateLockState>(lockRow).Value.Owners.Count == 0
            && snapshot.ToJson() == (await frozen.Find(new BsonDocument("target", fixture.Report.Id)).SingleAsync(ct)).ToJson(),
            "return releases its source owner, restores draft and preserves historical frozen snapshot");
        Check((await db.WorkAssignmentReports.Find(r => r.Id == fixture.Source.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Approved,
            "parent return leaves child report Approved");
        var payload = new WorkReportPayloadService(db);
        var store = new AggregateMongoStore(db, runner, payload, payload);
        var refresh = new AggregateRefreshService(store, new AggregateMongoCommandReader(db, payload, AggregateIntegrationGate.V2Ready(config)));
        var priorRevision = (await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct)).PayloadRevision;
        var result = await refresh.RunAsync(instanceId, state.Generation, ct);
        Check(result == "COMPLETED" && (await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct)).PayloadRevision == priorRevision + 1,
            "return intent refreshes only the fixture draft through real native writer");
        Check(await refresh.RunAsync(instanceId, state.Generation, ct) == "NO_WORK"
            && (await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct)).PayloadRevision == priorRevision + 1,
            "completed refresh retry does not write twice");
        Check(snapshot.ToJson() == (await frozen.Find(new BsonDocument("target", fixture.Report.Id)).SingleAsync(ct)).ToJson(),
            "draft refresh leaves frozen historical evidence unchanged");
    }
}
