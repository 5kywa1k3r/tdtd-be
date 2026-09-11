namespace tdtd_be.Services.WorkAssignments.Runtime;

public interface IWorkAssignmentStatusSyncService
{
    Task SyncFromAssignmentAsync(string workAssignmentId, CancellationToken ct = default);

    Task SyncFromAssignmentIdempotentAsync(
        string workAssignmentId,
        string idempotencyKey,
        CancellationToken ct = default);
}
