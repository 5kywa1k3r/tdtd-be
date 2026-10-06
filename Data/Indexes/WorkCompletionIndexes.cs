using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.Data.Indexes;

public static class WorkCompletionIndexes
{
    public const string CollectionName = "work_assignment_completion_requests";
    public static Task EnsureAsync(IMongoDatabase db, CancellationToken ct = default)
    {
        var collection = db.GetCollection<WorkAssignmentCompletionRequest>(CollectionName);
        return collection.Indexes.CreateManyAsync([
            new(Builders<WorkAssignmentCompletionRequest>.IndexKeys.Ascending(x => x.AssignmentId).Descending(x => x.RequestedAtUtc),
                new CreateIndexOptions { Name = "assignment_history" }),
            new(Builders<WorkAssignmentCompletionRequest>.IndexKeys.Ascending(x => x.WorkId).Ascending(x => x.State),
                new CreateIndexOptions { Name = "work_completion_state" })
        ], ct);
    }
}
