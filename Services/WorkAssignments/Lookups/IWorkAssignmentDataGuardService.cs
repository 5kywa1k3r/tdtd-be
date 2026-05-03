namespace tdtd_be.Services.WorkAssignments.Lookups;

public interface IWorkAssignmentDataGuardService
{
    Task<bool> HasAssignmentDataAsync(
        string workAssignmentId,
        CancellationToken ct = default);

    Task<HashSet<string>> GetAssignmentIdsHavingDataAsync(
        IEnumerable<string> workAssignmentIds,
        CancellationToken ct = default);
}