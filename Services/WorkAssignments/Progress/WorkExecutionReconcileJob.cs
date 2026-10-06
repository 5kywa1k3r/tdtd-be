using Hangfire;
using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.WorkAssignments.Progress;

// Re-read canonical sources on every retry; no report lifecycle writes or descendant completion.
public sealed class WorkExecutionReconcileJob(MongoDbContext db, WorkCompletionWorkflowService workflow,
    ILogger<WorkExecutionReconcileJob> log)
{
    [DisableConcurrentExecution(600)]
    public async Task RunAsync(CancellationToken ct)
    {
        using var cursor = await db.WorkAssignments.Find(a => !a.IsDeleted && a.IsActive && a.FlowInstanceId == null)
            .Project(a => new { a.Id, a.WorkId }).ToCursorAsync(ct);
        var works = new HashSet<string>();
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var row in cursor.Current)
            {
                ct.ThrowIfCancellationRequested();
                works.Add(row.WorkId);
                try { await workflow.ReconcileAssignmentAsync(row.Id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { log.LogWarning(ex, "Progress reconciliation pending for {AssignmentId}", row.Id); }
            }
        }
        var pendingWorks = await db.Works.Find(w => !w.IsDeleted && w.CompletionProjectionPending)
            .Project(w => w.Id).ToListAsync(ct);
        works.UnionWith(pendingWorks);
        foreach (var workId in works)
        {
            try { await workflow.ReconcileWorkAsync(workId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { log.LogWarning(ex, "Work progress reconciliation pending for {WorkId}", workId); }
        }
    }
}
