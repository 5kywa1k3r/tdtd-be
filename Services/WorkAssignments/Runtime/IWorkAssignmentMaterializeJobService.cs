using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public interface IWorkAssignmentMaterializeJobService
{
    Task EnqueueOrTouchAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default);

    Task EnqueueOrTouchByAssignmentIdAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default);

    Task<int> ProcessPendingJobsAsync(
        int maxJobs = 10,
        int batchSize = 20,
        CancellationToken ct = default);
    // Audit actor: a user ObjectId, or null for an autonomous system operation.
    Task DisableByAssignmentIdAsync(string workAssignmentId, string? actorUserId, CancellationToken ct = default);
}
