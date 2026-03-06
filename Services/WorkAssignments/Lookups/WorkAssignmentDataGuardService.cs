namespace tdtd_be.Services.WorkAssignments.Lookups;

public sealed class WorkAssignmentDataGuardService : IWorkAssignmentDataGuardService
{
    public async Task<bool> HasAssignmentDataAsync(string assignmentId, CancellationToken ct = default)
    {
        // TODO: nối collection report/submission thật
        await Task.CompletedTask;
        return false;
    }

    public async Task<HashSet<string>> GetAssignmentIdsHavingDataAsync(List<string> assignmentIds, CancellationToken ct = default)
    {
        // TODO: batch check thật
        await Task.CompletedTask;
        return new HashSet<string>(StringComparer.Ordinal);
    }
}