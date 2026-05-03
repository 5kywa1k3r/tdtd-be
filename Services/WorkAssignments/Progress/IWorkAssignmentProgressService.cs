using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Progress;

public interface IWorkAssignmentProgressService
{
    Task<ProgressRecomputeResult> RecomputeSingleAsync(string workAssignmentId, CancellationToken ct);
    Task<ProgressRecomputeResult> RecomputeSingleAsync(WorkAssignment assignment, CancellationToken ct);
    Task<List<ProgressRecomputeResult>> RecomputeDirectChildrenAsync(string parentAssignmentId, CancellationToken ct);
    Task<List<ProgressRecomputeResult>> RecomputeParentChainAsync(string workAssignmentId, CancellationToken ct);

    Task<ProgressComputeResult> ComputeProgressAsync(WorkAssignment assignment, CancellationToken ct);
    Task<ProgressComputeResult> ComputeLeafProgressAsync(WorkAssignment assignment, CancellationToken ct);
    Task<ProgressComputeResult> ComputeParentProgressAsync(WorkAssignment parent, List<WorkAssignment> directChildren, CancellationToken ct);
}