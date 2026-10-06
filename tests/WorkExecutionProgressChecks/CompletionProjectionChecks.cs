using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;

// Real workflow + queue/materializer methods, intercepted only at Mongo collection boundaries.
// Driver serialization is real; no MongoClient, network, host, or database is used.
internal static class CompletionProjectionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var original = new CompletionFixture();
        var reproduced = false;
        try { await original.Queue.DisableByAssignmentAsync(original.Assignment.Id, "system"); }
        catch (FormatException) { reproduced = true; }
        check(reproduced, "Real queue BSON update rejects the original system actor");

        var manual = new CompletionFixture();
        await manual.ConvergeAsync();
        check(!manual.Assignment.CompletionProjectionPending && manual.Writes.Count == 2,
            "Manual completion serializes queue and materializer writes before clearing pending");
        check(manual.Writes[0]["$set"]["updatedByUserId"].AsObjectId == ObjectId.Parse(manual.Actor) &&
              manual.Writes[1]["$set"]["updatedByUserId"].AsString == manual.Actor,
            "Manual completion preserves reviewer identity as queue ObjectId and materializer string");
        check(manual.Events.SequenceEqual(new[] { "queue", "materializer", "sync", "projection", "period-projection", "period-projection", "period-projection", "settle" }),
            "Pending is cleared only after all completion post-processing succeeds");
        check(manual.ProjectionActor == manual.Actor, "Doc-role projection receives the actual manual actor");

        var auto = new CompletionFixture();
        auto.Assignment.CompletedByUserId = null;
        auto.Assignment.CompletionMode = "AUTO_REPORTS_AND_DEADLINE";
        auto.FailOnceAt = "sync";
        await auto.ConvergeAsync();
        check(auto.Assignment.CompletionProjectionPending, "Autonomous completion failure preserves pending for job retry");
        await auto.ConvergeAsync();
        check(!auto.Assignment.CompletionProjectionPending &&
              auto.Writes.All(x => x["$set"]["updatedByUserId"].IsBsonNull),
            "Autonomous completion uses BSON null, without a fake system account");

        foreach (var failureStage in new[] { "queue", "materializer", "sync", "projection", "period-projection" })
        {
            var retry = new CompletionFixture { FailOnceAt = failureStage };
            await retry.ConvergeAsync();
            check(retry.Assignment.CompletionProjectionPending && !retry.Events.Contains("settle"),
                failureStage + " failure preserves pending and does not claim convergence");
            await retry.ConvergeAsync();
            check(!retry.Assignment.CompletionProjectionPending &&
                  retry.Writes.All(x => x["$set"]["updatedByUserId"].ToString() == retry.Actor),
                failureStage + " retry recovers using original business actor after snapshot audit changes");
        }

        var reopen = new CompletionFixture();
        reopen.Reopen();
        reopen.FailOnceAt = "projection";
        await reopen.ConvergeAsync();
        check(reopen.Assignment.CompletionProjectionPending, "Reopen projection failure retains pending");
        await reopen.ConvergeAsync();
        check(!reopen.Assignment.CompletionProjectionPending && reopen.Writes.Count == 2 &&
              reopen.Writes.All(x => x["$set"]["updatedByUserId"].AsObjectId == ObjectId.Parse(reopen.Actor) &&
                                    x["$setOnInsert"]["createdByUserId"].AsObjectId == ObjectId.Parse(reopen.Actor)),
            "Reopen upsert and retry serialize committed receipt actor even after UpdatedByUserId is erased");
        check(!reopen.Events.Contains("materializer"), "Reopen does not enqueue new materialization obligations");

        var missing = new CompletionFixture();
        missing.Reopen(); missing.Receipt = null;
        await missing.ConvergeAsync();
        check(missing.Assignment.CompletionProjectionPending && missing.Events.Count == 0,
            "Missing reopen receipt fails visibly without inventing an actor or clearing pending");

        var invalid = new CompletionFixture();
        invalid.Assignment.CompletedByUserId = "system";
        await invalid.ConvergeAsync();
        check(invalid.Assignment.CompletionProjectionPending && invalid.Events.Count == 0,
            "Invalid committed actor cannot silently become a system action");

        var race = new CompletionFixture();
        race.AfterSync = () => race.Reopen();
        await race.ConvergeAsync();
        check(!race.Assignment.CompletionProjectionPending && race.Events.Count(x => x == "settle") == 2 &&
              race.Writes.Last()["$set"].AsBsonDocument.Contains("lastObservedPeriodStatus"),
            "Revision mismatch re-reads reopen state and actor before acknowledging convergence");

        var cancel = new CompletionFixture { CancelAtSync = true };
        var cancelled = false;
        try { await cancel.ConvergeAsync(); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && cancel.Assignment.CompletionProjectionPending,
            "Cancellation propagates and keeps committed work pending for retry");
    }
}

internal sealed class CompletionFixture
{
    public string Actor { get; } = "6ac411b45e204bc3cab29b4b";
    public WorkAssignment Assignment { get; }
    public WorkAssignmentCompletionRequest? Receipt { get; set; }
    public string? FailOnceAt { get; set; }
    public bool CancelAtSync { get; set; }
    public Action? AfterSync { get; set; }
    public List<string> Events { get; } = [];
    public List<BsonDocument> Writes { get; } = [];
    public string? ProjectionActor { get; private set; }
    public WorkAssignmentQueueService Queue { get; }
    private readonly WorkCompletionWorkflowService workflow;
    private readonly WorkReportPeriod period;

    public CompletionFixture()
    {
        Assignment = new() { Id = "6ac411b45e204bc3cab29b4a", WorkId = "6ac40f855e204bc3cab298b5",
            IsActive = true, CompletedAtUtc = DateTime.UtcNow, CompletedByUserId = Actor, ProgressStatus = 2,
            CompletionRevision = 2, CompletionProjectionPending = true, CompletionMode = "APPROVED_REQUEST" };
        period = new() { Id = "6ac411b45e204bc3cab29b4c", WorkAssignmentId = Assignment.Id,
            WorkId = Assignment.WorkId, AssigneeUserId = "6ac411b45e204bc3cab29b4d", PeriodKey = "20261006", IsActive = true };
        var db = (MongoDbContext)RuntimeHelpers.GetUninitializedObject(typeof(MongoDbContext));
        Set(db, nameof(db.WorkAssignments), Collection<WorkAssignment>(() =>
            [new() { Id = Assignment.Id, WorkId = Assignment.WorkId, IsActive = Assignment.IsActive,
                CompletedAtUtc = Assignment.CompletedAtUtc, CompletedByUserId = Assignment.CompletedByUserId,
                ProgressStatus = Assignment.ProgressStatus, CompletionRevision = Assignment.CompletionRevision,
                CompletionReviewPeriodId = Assignment.CompletionReviewPeriodId,
                CompletionReopenedAtUtc = Assignment.CompletionReopenedAtUtc,
                CompletionProjectionPending = Assignment.CompletionProjectionPending }], (filter, update) =>
            {
                Stage("settle");
                var matches = filter["completionRevision"].ToInt64() == Assignment.CompletionRevision;
                if (matches) Assignment.CompletionProjectionPending = update["$set"]["completionProjectionPending"].AsBoolean;
                return matches;
            }));
        Set(db, nameof(db.WorkReportPeriods), Collection<WorkReportPeriod>(() => [period,
            new() { Id = "6ac411b45e204bc3cab29b5c", WorkAssignmentId = Assignment.Id },
            new() { Id = "6ac411b45e204bc3cab29b6c", WorkAssignmentId = Assignment.Id }]));
        Set(db, nameof(db.Works), Collection<Work>(() => [new() { Id = Assignment.WorkId, Status = WorkStatus.S3 }]));
        Set(db, nameof(db.WorkAssignmentQueueItems), Collection<WorkAssignmentQueueItem>(() => [], (_, update) =>
            { Stage("queue"); Writes.Add(update); return true; }));
        Set(db, nameof(db.WorkAssignmentMaterializeJobs), Collection<WorkAssignmentMaterializeJobs>(() => [], (_, update) =>
            { Stage("materializer"); Writes.Add(update); return true; }));
        var receipts = Collection<WorkAssignmentCompletionRequest>(() => Receipt == null ? [] : [Receipt], onRead: filter =>
        {
            // Ensure the production lookup binds to this exact reopen, not an arbitrary historical reviewer.
            if (filter["AssignmentId"] != Assignment.Id || filter["State"] != "REOPENED" ||
                filter["DecisionReason"] != Assignment.CompletionReviewPeriodId ||
                filter["DecidedAtUtc"].ToUniversalTime() != Assignment.CompletionReopenedAtUtc)
                throw new Exception("Receipt query did not identify the committed reopen");
        });
        Set(db, nameof(db.Db), CompletionProxy.For<IMongoDatabase>((method, args) =>
            method.Name == "GetCollection" && args![0] as string == tdtd_be.Data.Indexes.WorkCompletionIndexes.CollectionName
                ? receipts : throw new Exception("Unexpected database call: " + method.Name)));
        var sync = CompletionProxy.For<IWorkAssignmentStatusSyncService>((method, _) =>
        {
            if (method.Name != nameof(IWorkAssignmentStatusSyncService.SyncFromAssignmentAsync)) throw new Exception(method.Name);
            Stage("sync");
            if (CancelAtSync) throw new OperationCanceledException();
            Assignment.UpdatedByUserId = null;
            var action = AfterSync; AfterSync = null; action?.Invoke();
            return Task.CompletedTask;
        });
        var projection = CompletionProxy.For<IDocRoleReadModelProjectionService>((method, args) =>
        {
            if (method.Name == nameof(IDocRoleReadModelProjectionService.RebuildReportPeriodAsync))
            {
                if ((string?)args![1] != (Assignment.CompletedByUserId ?? Receipt?.DecidedByUserId ?? "system")) throw new Exception("Wrong period projection actor");
                Stage("period-projection"); return Task.CompletedTask;
            }
            if (method.Name != nameof(IDocRoleReadModelProjectionService.RebuildAssignmentAsync)) throw new Exception(method.Name);
            Stage("projection"); ProjectionActor = (string?)args![1]; return Task.CompletedTask;
        });
        Queue = new(db);
        var materialize = new WorkAssignmentMaterializeJobService(db, null!, sync, projection, null!,
            NullLogger<WorkAssignmentMaterializeJobService>.Instance, new ConfigurationBuilder().Build());
        workflow = new(db, null!, sync, projection, NullLogger<WorkCompletionWorkflowService>.Instance, Queue, materialize);
    }

    public void Reopen()
    {
        Assignment.CompletedAtUtc = null; Assignment.CompletedByUserId = null; Assignment.ProgressStatus = 1;
        Assignment.CompletionReviewPeriodId = period.Id;
        Assignment.CompletionReopenedAtUtc = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
        Assignment.CompletionRevision++; Assignment.CompletionProjectionPending = true;
        Receipt = new() { AssignmentId = Assignment.Id, State = "REOPENED", DecidedByUserId = Actor,
            DecidedAtUtc = Assignment.CompletionReopenedAtUtc, DecisionReason = period.Id };
    }

    public Task ConvergeAsync() => workflow.TryConvergeAsync(Assignment.Id, default);
    private void Stage(string name)
    {
        Events.Add(name);
        if (FailOnceAt == name) { FailOnceAt = null; throw new InvalidOperationException("Injected " + name + " failure"); }
    }
    private static void Set(MongoDbContext db, string property, object value) => typeof(MongoDbContext)
        .GetField("<" + property + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(db, value);

    private static IMongoCollection<T> Collection<T>(Func<List<T>> read,
        Func<BsonDocument, BsonDocument, bool>? write = null, Action<BsonDocument>? onRead = null) =>
        CompletionProxy.For<IMongoCollection<T>>((method, args) =>
        {
            var serializer = BsonSerializer.SerializerRegistry.GetSerializer<T>();
            var render = new RenderArgs<T>(serializer, BsonSerializer.SerializerRegistry);
            if (method.Name == "get_DocumentSerializer") return serializer;
            if (method.Name == "get_Settings") return new MongoCollectionSettings();
            if (method.Name == "FindAsync")
            {
                var filter = ((FilterDefinition<T>)args![0]!).Render(render); onRead?.Invoke(filter);
                return Task.FromResult<IAsyncCursor<T>>(new CompletionCursor<T>(read()));
            }
            if (method.Name is "UpdateOneAsync" or "UpdateManyAsync")
            {
                // Exercise the actual BSON serializers on updates constructed by production services.
                var filter = ((FilterDefinition<T>)args![0]!).Render(render);
                var update = ((UpdateDefinition<T>)args[1]!).Render(render).AsBsonDocument;
                var matched = (write ?? throw new Exception("Unexpected write"))(filter, update) ? 1 : 0;
                return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(matched, matched, null));
            }
            throw new Exception("Unexpected collection call: " + method.Name);
        });
}

internal sealed class CompletionCursor<T>(List<T> rows) : IAsyncCursor<T>
{
    private bool read;
    public IEnumerable<T> Current => rows;
    public bool MoveNext(CancellationToken cancellationToken = default) { if (read) return false; read = true; return rows.Count > 0; }
    public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default) => Task.FromResult(MoveNext(cancellationToken));
    public void Dispose() { }
}

internal class CompletionProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> handler = null!;
    public static T For<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, CompletionProxy>(); ((CompletionProxy)(object)proxy).handler = handler; return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => handler(method!, args);
}
