using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Lookups;

public interface IWorkAssignmentLookupService
{
    Task<Work> LoadWorkAsync(string workId, CancellationToken ct = default);
    Task<WorkAssignment> LoadAssignmentAsync(string assignmentId, CancellationToken ct = default);
    Task<WorkAssignment?> LoadParentAsync(string? parentAssignmentId, string workId, CancellationToken ct = default);
    Task EnsureParentExistsAsync(string parentAssignmentId, CancellationToken ct = default);
}