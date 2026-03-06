namespace tdtd_be.Services.WorkAssignments.Lookups;

public interface IWorkAssignmentDataGuardService
{
    Task<bool> HasAssignmentDataAsync(string assignmentId, CancellationToken ct = default);
    Task<HashSet<string>> GetAssignmentIdsHavingDataAsync(List<string> assignmentIds, CancellationToken ct = default);
}