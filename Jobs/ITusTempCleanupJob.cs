namespace tdtd_be.Jobs;

public interface ITusTempCleanupJob
{
    Task RunAsync(CancellationToken ct = default);
}
