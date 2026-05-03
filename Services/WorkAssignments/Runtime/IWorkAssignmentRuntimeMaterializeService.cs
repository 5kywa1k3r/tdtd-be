namespace tdtd_be.Services.WorkAssignments.Runtime;

public interface IWorkAssignmentRuntimeMaterializeService
{
    Task MaterializeForAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default);
    Task RematerializeForAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default);
}
