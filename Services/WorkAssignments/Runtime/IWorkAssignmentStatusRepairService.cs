namespace tdtd_be.Services.WorkAssignments.Runtime;

public interface IWorkAssignmentStatusRepairService
{
    Task RebuildWorkTreeAsync(string workId, CancellationToken ct = default);
}
