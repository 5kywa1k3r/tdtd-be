namespace tdtd_be.Services.WorkAssignments.Queue;

public interface IWorkAssignmentQueueJobService
{
    Task ScanDuePeriodsAsync(CancellationToken ct = default);
}
